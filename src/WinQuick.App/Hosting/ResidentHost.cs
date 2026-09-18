using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinQuick.App.Diagnostics;
using WinQuick.App.Design;
using WinQuick.App.Radial;
using WinQuick.App.Tray;
using WinQuick.App.Actions;
using WinQuick.App.Config;
using WinQuick.App.Interop;
using WinQuick.App.Settings;
using WinQuick.Core.Config;
using WinQuick.Core.Geometry;
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
        _overlay.AddItemRequested += OnAddItemRequested;
        _overlay.SectorEditRequested += OnSectorEditRequested;
        _overlay.AddGroupRequested += OnAddGroupRequested;
        _overlay.ReorderRequested += OnReorderRequested;
        _overlay.ExitRequested += (_, _) => Application.Current.Shutdown(0);
        _overlay.SettingsChanged += (_, config) => OnSettingsChanged(config);
        _overlay.SkinChanged += (_, e) => OnSkinChanged(e.SkinId, e.Form);
        _overlay.SetHotkeyProbe(spec => _hotkeys.ProbeTemporarily(spec));
        _overlay.SetSettingsSource(_config.Config);
        ActionExecutor.Completed += OnActionCompleted;

        _session.SessionLocked += (_, _) => _overlay.Hide();
        _session.SessionUnlocked += OnSessionUnlocked;

        _hookHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = HookHealthInterval,
        };
        _hookHealthTimer.Tick += (_, _) =>
        {
            CheckHookHealth();
            EnsureShellMenuInstalled();
        };

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

        // 皮肤与形态要在**预热之前**装上：预热会真的渲染一次轮盘，
        // 那一次就该是用户配置的样子（否则第一次唤出会先看到默认皮肤再跳变）。
        ApplyConfiguredAppearance();

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

        // 右键菜单项与当前菜单对齐。启动时做一次：程序可能被移动过，
        // 而注册表里还留着指向旧路径的命令（那种菜单项点了毫无反应）。
        SyncShellMenu();

        // 启动我们的那次右键点击可能留下了请求（没有实例在跑时就是这条路径）。
        ApplyPendingShellRequest();

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
    /// 用户点了环里的「＋」：弹文件浏览器，把选中的程序加进那一级。
    ///
    /// 关键是**加完不关闭覆盖层**。用户是在菜单里就地编辑，加完应当立刻看见新扇区长出来；
    /// 关掉的话他要重新按热键、再找一遍位置，等于把"就地"这个好处丢掉了。
    ///
    /// 写盘会触发热重载 → 走既有的"内容变了才重建轮盘"那条路径，
    /// 重建保留位置（见 RebuildForMenu），所以轮盘不会跳到光标处。
    /// </summary>
    private void OnAddItemRequested(object? sender, int parentIndex)
    {
        // 上限与索引合法性都在 MenuEditor 里判（那里还有单测），这里不重复一遍 ——
        // 校验写在两处，迟早会有一处忘了改。
        var path = _overlay.PickExecutableForMenu();
        if (path is null) return;          // 用户取消

        // 名称取程序的版本信息，与内置默认菜单同源：跟随系统语言，且不必用户自己想名字。
        var item = new MenuItemSpec
        {
            Label = ShellNames.Get(path),
            IconPath = path,
            Action = MenuAction.Launch(path),
        };

        var (updated, error) = MenuEditor.Add(_menu, parentIndex, item);
        if (error is not null)
        {
            _tray.ShowBalloon("WinQuick 无法添加", error);
            return;
        }

        // 加进子环的项，重建之后要把子弧重新展开 —— 否则用户看不到刚加的东西，
        // 会以为没加上。第一层不需要（它本来就全在屏幕上）。
        _reopenChildArcAfterRebuild = parentIndex;

        PersistMenu(updated);
    }

    /// <summary>
    /// 拖拽重排：把某一层里的项挪到新位置。
    ///
    /// 排列就是菜单里的数组顺序，扇区角度由项数均匀分配 —— 所以"改排列"只是挪数组，
    /// 不新增任何字段，menus.json 里的顺序也始终等于盘面上的顺序。
    /// </summary>
    private void OnReorderRequested(object? sender, (int ParentIndex, int From, int To) request)
    {
        var (updated, error) = MenuEditor.Move(_menu, request.ParentIndex, request.From, request.To);
        if (error is not null)
        {
            _tray.ShowBalloon("WinQuick 无法调整顺序", error);
            return;
        }

        // 顺序没变时 Move 会原样返回，这里就不写盘 —— 一次没挪动的拖拽不该产生一次写盘。
        if (ReferenceEquals(updated, _menu)) return;

        // 子层重排后重建，把子弧重新展开：用户正在看的就是它，收起会让人以为操作没生效。
        if (request.ParentIndex >= 0) _reopenChildArcAfterRebuild = request.ParentIndex;

        PersistMenu(updated);
    }

    /// <summary>
    /// 新建一个分组。分两步：名称已经在轮盘的面板里问过了，这里再让用户选**第一项**。
    ///
    /// 为什么必须有第一项：空分组会渲染成一个既无子项、又无动作的扇区，
    /// 点它什么都不发生、悬停也不展开东西，而用户看不出为什么。
    /// 两步之间用户取消选程序，就整个放弃 —— 不留一个残缺的分组。
    /// </summary>
    private void OnAddGroupRequested(object? sender, (int ParentIndex, string Label) request)
    {
        var path = _overlay.PickExecutableForMenu();
        if (path is null) return;          // 用户取消

        var firstChild = new MenuItemSpec
        {
            Label = ShellNames.Get(path),
            IconPath = path,
            Action = MenuAction.Launch(path),
        };

        var (updated, error) = MenuEditor.AddGroup(_menu, request.Label, firstChild);
        if (error is not null)
        {
            _tray.ShowBalloon("WinQuick 无法新建分组", error);
            return;
        }

        PersistMenu(updated);
    }

    /// <summary>
    /// 扇区编辑面板上的改名 / 移除。
    ///
    /// 与「＋」一样：改完不关闭覆盖层，让用户当场看到结果 ——
    /// 改完一项名称却发现整个菜单没了，是这类就地编辑最差的一种反馈。
    /// </summary>
    private void OnSectorEditRequested(
        object? sender, (int RootIndex, int ChildIndex, Radial.SectorEditAction Action, string? Label) request)
    {
        var (updated, error) = request.Action switch
        {
            Radial.SectorEditAction.Remove =>
                MenuEditor.Remove(_menu, request.RootIndex, request.ChildIndex),
            Radial.SectorEditAction.Rename =>
                MenuEditor.Rename(_menu, request.RootIndex, request.ChildIndex, request.Label),
            _ => (_menu, "未知的编辑动作。"),
        };

        if (error is not null)
        {
            _tray.ShowBalloon("WinQuick 无法修改菜单", error);
            return;
        }

        // 移除子项后重建：如果那一项变成没有子项的叶子，子弧本来也展不开了；
        // 还有子项时把子弧重新展开，用户能看见"少了一个、剩下的还在"。
        if (request.ChildIndex >= 0) _reopenChildArcAfterRebuild = request.RootIndex;

        PersistMenu(updated);
    }

    /// <summary>重建轮盘之后要重新展开的子弧父项；-1 表示不用。</summary>
    private int _reopenChildArcAfterRebuild = -1;

    /// <summary>
    /// 自愈：注册项被外部删掉就补回来。
    ///
    /// 为什么需要它：注册项只在启动、菜单变化、开关变化时写一次，而它是**别人地盘上的东西** ——
    /// 其它右键菜单工具、清理软件、或任何"重置关联"的操作都可能把它删掉，
    /// 而那时程序不会有任何察觉，用户只看到"菜单项莫名其妙没了"。
    /// 这正是本功能第一次上线后遇到的状况（删掉它的恰好是我自己的探针）。
    ///
    /// 代价是每 5 秒一次 OpenSubKey —— 可以忽略。做成自愈比做成"记得别删"可靠。
    /// </summary>
    private void EnsureShellMenuInstalled()
    {
        if (!_config.Config.ShellMenu) return;

        var (installed, _) = Interop.ShellContextMenu.Query();
        if (installed) return;

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe)) SyncShellMenu();
    }

    /// <summary>上一次写进注册表时的"开关 + 程序路径"，用来判断要不要重写。</summary>
    private string _shellMenuSignature = "";

    /// <summary>
    /// 把右键菜单项对齐到当前菜单。
    ///
    /// 注册表方式是**纯数据**，Explorer 不会替我们算目的地列表 ——
    /// 所以菜单一变就得整棵重写。反过来这也是它的好处：没有常驻在 Explorer 里的代码，
    /// 我们崩了也不会把资源管理器带崩。
    /// </summary>
    private void SyncShellMenu()
    {
        if (!_config.Config.ShellMenu)
        {
            var (removed, removeError) = Interop.ShellContextMenu.Uninstall();
            if (!removed)
                _tray.ShowBalloon("WinQuick 无法移除右键菜单项", removeError ?? "未知原因");
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;

        var (wasInstalled, registeredExe) = Interop.ShellContextMenu.Query();
        var exeMoved = wasInstalled
                       && !string.Equals(registeredExe, exe, StringComparison.OrdinalIgnoreCase);

        var (ok, error, note) = Interop.ShellContextMenu.Install(_menu, exe);
        if (!ok)
        {
            _tray.ShowBalloon("WinQuick 无法加入右键菜单", error ?? "未知原因");
            return;
        }

        _shellMenuSignature = $"{_config.Config.ShellMenu}|{exe}";

        if (note is not null) _tray.ShowBalloon("WinQuick 右键菜单", note);

        // 只在"第一次出现"和"指向变了"这两种情况下提示 ——
        // 每次启动都弹一遍的话，用户会把它当成噪音而不再读。
        if (!wasInstalled)
        {
            _tray.ShowBalloon("WinQuick 已加入文件右键菜单",
                "在右键菜单里找「WinQuick → 添加」。\n" +
                "Win11 的紧凑菜单不接受这种注册方式，请点「显示更多选项」或按 Shift+F10。");
        }
        else if (exeMoved)
        {
            _tray.ShowBalloon("WinQuick 右键菜单已更新", "程序位置变了，菜单命令已指向新位置。");
        }
    }

    /// <summary>
    /// 消费一条待处理的右键菜单请求。
    ///
    /// 取走即删除，所以重入是安全的：本方法会经由"保存菜单 → 重载 → Changed"再次被调到，
    /// 而那时文件已经不在了。不需要额外的重入标志。
    /// </summary>
    private void ApplyPendingShellRequest()
    {
        if (_config.TakeAddRequest() is not { } request) return;
        AddPathToLevel(request.Path, request.Value);
    }

    /// <summary>
    /// 把某个路径加进指定目的地。右键菜单与托盘都把落地动作收敛到这里 ——
    /// 分成两份的话，"菜单满了怎么办""分组改名了怎么办"这类规则迟早会分叉。
    /// </summary>
    private void AddPathToLevel(string path, string targetValue)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _tray.ShowBalloon("WinQuick 无法添加", "没有拿到文件路径。");
            return;
        }

        // 文件夹也算：右键菜单对文件与文件夹都注册了。
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _tray.ShowBalloon("WinQuick 无法添加", $"找不到「{path}」。");
            return;
        }

        if (!ShellTargets.TryResolve(_menu, targetValue, out var parentIndex, out var resolveError))
        {
            _tray.ShowBalloon("WinQuick 无法添加", resolveError ?? "无法确定要加到哪里。");
            return;
        }

        var item = new MenuItemSpec
        {
            Label = ShellNames.Get(path),
            IconPath = path,
            Action = MenuAction.Launch(path),
        };

        var (updated, error) = MenuEditor.Add(_menu, parentIndex, item);
        if (error is not null)
        {
            _tray.ShowBalloon("WinQuick 无法添加", error);
            return;
        }

        _reopenChildArcAfterRebuild = parentIndex;
        PersistMenu(updated);

        _tray.ShowBalloon("已添加到 WinQuick",
            $"「{item.Label}」→ {ShellTargets.Describe(updated, parentIndex)}");
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

        // 请求也可能是**由这次重载带来的**：常驻实例在监视配置目录，
        // 右键菜单那个短命进程把请求写进目录后，正是靠这次重载把它送进来。
        ApplyPendingShellRequest();

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

            if (_reopenChildArcAfterRebuild >= 0)
            {
                if (_overlay.IsVisible) _overlay.ReopenChildArc(_reopenChildArcAfterRebuild);
                _reopenChildArcAfterRebuild = -1;
            }

            // 菜单变了，右键菜单里那串目的地也就过时了，重写一次。
            SyncShellMenu();
        }

        // 开关或程序路径变了也要重写（后者关系到注册表里那条命令指向何处）。
        var shellSignature = $"{_config.Config.ShellMenu}|{Environment.ProcessPath}";
        if (shellSignature != _shellMenuSignature)
        {
            _shellMenuSignature = shellSignature;
            SyncShellMenu();
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

    /// <summary>
    /// 皮肤面板里选了皮肤/形态。
    ///
    /// 顺序是刻意的：**先换令牌、再重建轮盘、最后排队落盘**。
    /// · 换令牌（<see cref="TokenStore.ApplySkin"/>）会顺手重装应用资源，面板自己的配色也跟着变；
    /// · 重建轮盘走的是"复用画布"那条路径（实测零像素变化）—— 用户在面板上一个个点过去，
    ///   每次都必须立刻看到，且不能闪；
    /// · 落盘走与设置面板同一条去抖（500ms），免得连点时写十几次盘。
    /// </summary>
    public void OnSkinChanged(string skinId, RingForm form)
    {
        var problem = TokenStore.ApplySkin(skinId);

        // 应用的令牌要能立刻影响运行中的窗口：菜单重建/子弧这些路径读的是
        // TokenStore.Current，而轮盘把令牌缓存在自己身上，所以必须显式重建。
        _overlay.RebuildForAppearance(form);

        _pendingSettings = _config.Config with
        {
            Appearance = _config.Config.Appearance with { Skin = skinId, Form = RingForms.IdOf(form) },
        };
        _saveDebounce.Stop();
        _saveDebounce.Start();

        if (problem is not null)
            CrashLog.Write("皮肤回退", new InvalidOperationException(problem));
    }

    /// <summary>
    /// 启动时把配置里记着的皮肤与形态装上。
    ///
    /// 必须在建轮盘**之前**装皮肤（窗口构造时会抓一份令牌），
    /// 形态则在轮盘建好之后用一次重建落定。
    /// </summary>
    private void ApplyConfiguredAppearance()
    {
        var appearance = _config.Config.Appearance;

        var problem = TokenStore.ApplySkin(appearance.Skin);
        if (problem is not null)
            CrashLog.Write("皮肤回退", new InvalidOperationException(problem));

        _overlay.RebuildForAppearance(RingForms.Parse(appearance.Form));
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
