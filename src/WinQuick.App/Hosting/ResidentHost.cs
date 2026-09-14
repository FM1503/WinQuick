using System.Windows;
using System.Windows.Threading;
using WinQuick.App.Diagnostics;
using WinQuick.App.Tray;
using WinQuick.App.Actions;
using WinQuick.Core.Config;
using WinQuick.Core.Hotkey;

namespace WinQuick.App.Hosting;

/// <summary>
/// 常驻模式的装配：把热键、覆盖层、托盘、会话监听接在一起。
///
/// 启动顺序是有讲究的：
/// 1. 单实例检查放最前 —— 重复启动不该白做一遍预热再退出
/// 2. 覆盖层预热在注册热键之前 —— 保证第一次按下热键就已经是热的
/// 3. 热键最后注册 —— 它是唯一会立刻引入用户可见行为的环节
/// </summary>
public sealed class ResidentHost : IDisposable
{
    /// <summary>
    /// 钩子健康自检间隔。Win7+ 上钩子回调超时会被系统**静默摘掉**且不通知应用，
    /// 所以只能靠周期性重装来兜底。5 秒一次，检查本身是零成本的。
    /// </summary>
    private static readonly TimeSpan HookHealthInterval = TimeSpan.FromSeconds(5);

    private readonly SingleInstance _singleInstance = new();
    private readonly MenuSpec _menu;
    private readonly HotkeyManager _hotkeys;
    private readonly OverlayController _overlay;
    private readonly SessionWatcher _session;
    private readonly TrayHost _tray;
    private readonly DispatcherTimer _hookHealthTimer;

    private bool _disposed;

    public ResidentHost()
    {
        _hotkeys = new HotkeyManager();
        // M4 之前先用装配出来的默认菜单；配置系统完成后换成从 menus.json 读取。
        // 装配包含 UWP 应用的 shell 枚举，所以放在 App 层（Core 不依赖 Win32）。
        _menu = DefaultMenu.Build();
        _overlay = new OverlayController(_menu);
        _session = new SessionWatcher();
        _tray = new TrayHost(_overlay);

        _hotkeys.Pressed += (_, _) => _overlay.Toggle();
        _overlay.ItemActivated += OnItemActivated;
        ActionExecutor.Completed += OnActionCompleted;

        _session.SessionLocked += (_, _) => _overlay.Hide();
        _session.SessionUnlocked += OnSessionUnlocked;

        _hookHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = HookHealthInterval,
        };
        _hookHealthTimer.Tick += (_, _) => CheckHookHealth();
    }

    /// <summary>本进程是否是首个实例。false 时调用方应当直接退出。</summary>
    public bool IsFirstInstance => _singleInstance.IsFirstInstance;

    public void Start()
    {
        _singleInstance.ListenForActivation(() =>
            Application.Current.Dispatcher.BeginInvoke(() => _overlay.Show()));

        _overlay.Prewarm();

        var result = _hotkeys.Apply(HotkeySpec.Default);
        WarnIfHotkeyDegraded(result);

        _hookHealthTimer.Start();
    }

    /// <summary>
    /// 热键注册失败或降级到钩子时给出明确反馈。
    /// 这一步不能省：热键是这个工具唯一的入口，用户遇到 1409 却没有任何提示的话，
    /// 会以为程序坏了。托盘气泡 + 崩溃日志双写。
    /// </summary>
    private void WarnIfHotkeyDegraded(HotkeyApplyResult result)
    {
        if (!result.Success)
        {
            CrashLog.Write("热键注册失败", new InvalidOperationException(result.Message));
            _tray.ShowBalloon("WinQuick", "热键注册失败。" + result.Message);
            return;
        }

        if (result.NeedsLiveVerification)
        {
            // 钩子装上了不等于按键能到达 —— 系统层消费的组合会静默无效。
            // 所以这里只提示"需要确认"，不谎报成功。
            CrashLog.Write("热键降级到钩子", new InvalidOperationException(result.Message));
            _tray.ShowBalloon("WinQuick",
                $"已改用键盘钩子接管 {_hotkeys.Current}。请按一次确认有效；" +
                "若无效，说明该组合被系统占用，需要换一个。");
        }
    }

    /// <summary>
    /// 某个项被点击（第一层或子弧里的子项）。
    ///
    /// 顺序是刻意的：**先收界面，再执行动作**。
    /// 反过来的话，新启动的程序窗口会先冒出来、再被覆盖层挡住一下，观感像闪屏。
    /// 而且 Hide() 会立刻把前台焦点还给用户原来的窗口，这是「按下就立刻响应」的前提。
    /// </summary>
    private void OnItemActivated(object? sender, (int ParentIndex, int ChildIndex) selection)
    {
        if (selection.ParentIndex < 0 || selection.ParentIndex >= _menu.Items.Count) return;

        var item = _menu.Items[selection.ParentIndex];

        if (selection.ChildIndex >= 0)
        {
            // 选中的是子弧里的子项。
            if (selection.ChildIndex >= item.Children.Count) return;
            item = item.Children[selection.ChildIndex];
        }
        else if (item.HasChildren)
        {
            // 第一层里"有子项但没展开"的项被直接执行了 —— 不该发生，
            // 说明点击路径有问题。明确报出来而不是静默什么都不做。
            _tray.ShowBalloon("WinQuick", $"「{item.Label}」有子菜单，应展开而不是执行。");
            return;
        }

        _overlay.Hide();

        // ActionExecutor 内部走线程池，这里不会阻塞 —— 覆盖层的关闭路径必须立刻返回。
        ActionExecutor.Execute(item);
    }

    /// <summary>
    /// 动作失败必须让用户看见。
    /// 静默失败会被当成"程序坏了"，而这恰恰是最容易发生的事（路径不存在、权限不足）。
    /// </summary>
    private void OnActionCompleted(MenuItemSpec item, ActionResult result)
    {
        if (result.Success) return;
        _tray.ShowBalloon("WinQuick 无法执行", $"「{item.Label}」{result.Message}");
    }

    private void CheckHookHealth()
    {
        if (_hotkeys.ReinstallHookIfLost())
        {
            // 系统不会告诉我们钩子被摘掉了，只能自己发现 —— 记下来便于排查
            // "热键偶尔失灵"这类难复现的问题。
            CrashLog.Write("键盘钩子被系统摘掉，已重装",
                new InvalidOperationException($"第 {_hotkeys.HookInstallCount} 次安装"));
        }
    }

    private void OnSessionUnlocked(object? sender, EventArgs e)
    {
        // 解锁后必须强制收敛：锁屏期间可能残留着显示中的覆盖层，
        // 而且锁屏时按下的修饰键会在钩子的状态机里留下"卡住"的假状态。
        _overlay.ForceHideAndRebuild();
        _hotkeys.ReinstallHookIfLost();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _hookHealthTimer.Stop();
        ActionExecutor.Completed -= OnActionCompleted;
        _hotkeys.Dispose();
        _session.Dispose();
        _tray.Dispose();
        _overlay.Dispose();
        _singleInstance.Dispose();
    }
}
