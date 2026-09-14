using System.Windows;
using System.Windows.Threading;
using WinQuick.App.Diagnostics;
using WinQuick.App.Tray;
using WinQuick.App.Actions;
using WinQuick.App.Config;
using WinQuick.App.Settings;
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
    private readonly ConfigStore _config = new();
    private readonly ConfigReloadResult _initialLoad;

    /// <summary>配置写盘的去抖计时器。设置面板每拖一格滑块都会触发一次改动，
    /// 逐个写盘既无必要也伤磁盘。</summary>
    private readonly DispatcherTimer _saveDebounce;

    /// <summary>上一次已知的菜单内容签名，用来判断菜单是否**真的**变了。</summary>
    private string _menuSignature = "";
    private MenuSpec _menu;
    private readonly HotkeyManager _hotkeys;
    private readonly OverlayController _overlay;
    private readonly SessionWatcher _session;
    private readonly TrayHost _tray;
    private readonly DispatcherTimer _hookHealthTimer;

    private bool _disposed;

    public ResidentHost()
    {
        _hotkeys = new HotkeyManager();
        // 先读配置。首次运行会写出一份带注释的 config.json，
        // 以及一份由当前装配结果序列化出来的 menus.json（填好真实路径，可直接改）。
        _initialLoad = _config.LoadOrCreateDefaults();
        _menu = _config.Menu;

        _overlay = new OverlayController(_menu);
        _overlay.ApplyDimOpacity(_config.Config.Appearance.DimOpacity);
        _session = new SessionWatcher();
        _tray = new TrayHost(_overlay);

        _hotkeys.Pressed += (_, _) => _overlay.Toggle();
        _overlay.ItemActivated += OnItemActivated;
        _overlay.ExitRequested += (_, _) => Application.Current.Shutdown(0);
        _overlay.SettingsChanged += (_, config) => OnSettingsChanged(config);
        _overlay.SetHotkeyProbe(spec => _hotkeys.ProbeTemporarily(spec));
        _overlay.SetSettingsSource(_config.Config);
        _overlay.SetMenuSource(_menu);
        _overlay.MenuChanged += (_, menu) => PersistMenu(menu);
        ActionExecutor.Completed += OnActionCompleted;

        _session.SessionLocked += (_, _) => _overlay.Hide();
        _session.SessionUnlocked += OnSessionUnlocked;

        _hookHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = HookHealthInterval,
        };
        _hookHealthTimer.Tick += (_, _) => CheckHookHealth();

        _saveDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            PersistSettings();
        };
    }

    /// <summary>本进程是否是首个实例。false 时调用方应当直接退出。</summary>
    public bool IsFirstInstance => _singleInstance.IsFirstInstance;

    public void Start()
    {
        _singleInstance.ListenForActivation(() =>
            Application.Current.Dispatcher.BeginInvoke(() => _overlay.Show()));

        _overlay.Prewarm();

        // 事件驱动地重申置顶：只在「别人抢到前台」时动手，平时一次 SetWindowPos 都不发。
        // 无条件定时重申会让分层窗口持续重新合成 —— 那是闪烁的来源。
        _overlay.StartZOrderWatching();

        var hotkey = ResolveHotkey();
        var result = _hotkeys.Apply(hotkey, _config.Config.AllowHookFallback);
        WarnIfHotkeyDegraded(result);

        // 开机自启只在首次运行时按配置写一次。之后注册表就是事实来源 ——
        // 每次启动都按配置覆盖的话，用户从托盘关掉自启后，下次启动又会被配置打开。
        if (_config.DefaultsWereCreated) AutoStart.SetEnabled(_config.Config.Startup.AutoStart);

        _menuSignature = _config.SerializeMenu(_menu);

        _config.StartWatching();
        _config.Changed += OnConfigChanged;

        // 首次加载就有问题时也要提示 —— 但注意此时订阅的改动事件已经生效，
        // 后续每次热重载都会独立提示，不会漏报。
        if (!_initialLoad.AllOk) ReportConfigProblems(_initialLoad);

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

    /// <summary>热键来自配置；配置里没写或解析失败时用默认热键。</summary>
    private HotkeySpec ResolveHotkey()
    {
        if (string.IsNullOrWhiteSpace(_config.Config.Hotkey)) return HotkeySpec.Default;

        return HotkeySpec.TryParse(_config.Config.Hotkey, out var spec, out var error) && spec is not null
            ? spec
            : HotkeySpec.Default;
    }

    /// <summary>
    /// 配置热重载。
    ///
    /// 菜单变了要**重建轮盘窗口**（视觉树是构造时用菜单建立的）；
    /// 热键变了要重新注册。两者都失败也要保留上一份可用状态 ——
    /// 手写 JSON 出错太容易，不能让"改错配置"变成"程序不能用"。
    /// </summary>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        var problems = _config.LastProblems;

        // 菜单：仅在**内容**真的换了才重建窗口。
        //
        // 不能比引用：配置热重载每次都会解析出一个新的 MenuSpec 实例，
        // 而设置面板里的任何改动都会写一次配置、触发一次热重载 ——
        // 按引用比较会导致"调一下遮罩深浅就重建整个轮盘窗口"，
        // 预览被打断、还白付一次重建成本。
        var signature = _config.SerializeMenu(_config.Menu);
        if (!string.Equals(signature, _menuSignature, StringComparison.Ordinal))
        {
            _menuSignature = signature;
            _menu = _config.Menu;

            // 这里刻意**不关**设置面板：菜单变更正是面板里的操作引起的，
            // 重建轮盘恰好让用户立刻看到新项 —— 关掉面板反而把预览打断。
            // 面板是独立窗口，重建轮盘不影响它。Show() 里也判了"面板开着就不夺焦点"。
            _overlay.RebuildForMenu(_menu);
        }

        // 设置面板改的东西也要能在别处（比如手动改 config.json）生效。
        _overlay.ApplyRuntimeStyle(_config.Config);

        // 热键：仅在配置里的值变了才重新注册，避免无谓地注销再注册。
        var wanted = ResolveHotkey();
        if (wanted != _hotkeys.Current)
        {
            var result = _hotkeys.Apply(wanted, _config.Config.AllowHookFallback);
            WarnIfHotkeyDegraded(result);
        }

        if (problems.Count > 0) ReportConfigProblems(new ConfigReloadResult(true, true, problems));
    }

    private void ReportConfigProblems(ConfigReloadResult result)
    {
        _tray.ShowBalloon("WinQuick 配置有问题",
            result.Summary + "。已保留上一份有效配置，程序继续运行。");
    }

    /// <summary>
    /// 打开设置（托盘菜单与重复启动都会调用）。
    ///
    /// 设置面板开在**覆盖层会话里**，所以先把覆盖层唤出来。
    /// 这样面板与轮盘同时在场：拖遮罩滑块、改高亮色、调悬停延迟，
    /// 都能立刻在旁边的轮盘上看到效果 —— 独立对话框做不到这一点。
    /// </summary>
    public void OpenSettings()
    {
        _overlay.SetSettingsSource(_config.Config);
        _overlay.SetMenuSource(_menu);

        // 面板开在覆盖层会话里，所以先把覆盖层唤出来。
        // 从托盘打开时用户可能不在轮盘界面，这一步让他看到完整上下文。
        if (!_overlay.IsVisible) _overlay.Show();
        _overlay.OpenSettings();
    }

    /// <summary>
    /// 设置面板里的改动。先**立刻应用**外观（这是"改动即预览"的关键），
    /// 再**去抖写盘** —— 拖一次滑块会触发很多次改动，逐个写盘既无必要也伤磁盘。
    /// </summary>
    private void OnSettingsChanged(AppConfig config)
    {
        _overlay.SetSettingsSource(config);
        _overlay.ApplyRuntimeStyle(config);

        _pendingSettings = config;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private AppConfig? _pendingSettings;

    /// <summary>
    /// 设置面板里改了菜单：写回 menus.json。
    /// 写盘会触发热重载，进而走既有的"内容变了才重建轮盘"那条路径 ——
    /// 所以这里不必额外重建，用户改完立刻就能在轮盘上看到。
    /// </summary>
    private void PersistMenu(MenuSpec menu)
    {
        if (!_config.SaveMenu(menu, out var error))
        {
            _tray.ShowBalloon("WinQuick 无法保存菜单", error ?? "未知原因");
            return;
        }

        _menu = menu;
        _overlay.SetMenuSource(menu);
    }

    private void PersistSettings()
    {
        if (_pendingSettings is not { } config) return;
        _pendingSettings = null;

        var autoStartOk = AutoStart.SetEnabled(config.Startup.AutoStart);

        if (!_config.SaveConfig(config, out var error))
        {
            _tray.ShowBalloon("WinQuick 无法保存设置", error ?? "未知原因");
            return;
        }

        if (!autoStartOk)
            _tray.ShowBalloon("WinQuick", "开机自启写入注册表失败（可能被组策略或安全软件限制）。");
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
        _saveDebounce.Stop();
        PersistSettings();
        _config.Changed -= OnConfigChanged;
        _config.Dispose();
        ActionExecutor.Completed -= OnActionCompleted;
        _hotkeys.Dispose();
        _session.Dispose();
        _tray.Dispose();
        _overlay.Dispose();
        _singleInstance.Dispose();
    }
}
