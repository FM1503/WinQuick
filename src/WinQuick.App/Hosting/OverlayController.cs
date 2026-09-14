using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using WinQuick.Core.Hotkey;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Hosting;

/// <summary>
/// 覆盖层的编排：遮罩层 + 轮盘层的显示/隐藏、焦点保存还原、置顶维持、显示器变更重建。
///
/// 窗口在启动时就全部创建好并保持隐藏（<see cref="Prewarm"/>）。这是刻意的：
/// Kando 在 1.8.0 引入懒初始化后用户抱怨"首次打开明显变慢"，宁可退回旧版本。
/// 预热让热键回调只需要 Show + 启动动画，这是达成「按下到可见 &lt;100ms」的前提。
/// </summary>
public sealed class OverlayController : IDisposable
{
    /// <summary>
    /// 置顶重申间隔。WS_EX_TOPMOST 只保证压住非 topmost 窗口，
    /// 不高于其他 topmost（任务管理器、其他常驻置顶工具都在同一个 band 里）。
    /// 没有文档化的更高层级，所以只能周期性重申 —— 这是行业惯例做法。
    /// </summary>
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// 系统对话框弹出期间用的重申周期。
    /// 对话框的 topmost 只能靠事后补样式得到，越早补上越好。
    /// </summary>
    private static readonly TimeSpan FastTopmostReassertInterval = TimeSpan.FromMilliseconds(100);

    private void SetTopmostInterval(TimeSpan interval)
    {
        _topmostTimer.Interval = interval;

        // 时钟在跑时改 Interval 不会立刻重新计时，重启一次让新周期立即生效。
        if (_topmostTimer.IsEnabled)
        {
            _topmostTimer.Stop();
            _topmostTimer.Start();
        }

        // 立刻重申一次，不必等新周期。
        ReassertTopmost();
    }

    private readonly List<RawDimWindow> _dims = [];
    private RadialWindow _radial;   // 菜单热重载时会整体替换，所以不是 readonly
    private readonly OverlayActionBar _actionBar = new();

    // 设置面板按需创建：设置是低频操作，为它常驻一个窗口不值得。
    // 首次打开后保留，避免反复创建。
    private SettingsPanel? _settingsPanel;

    /// <summary>Hide/Show 往返之前设置面板是否打开着，用于恢复。</summary>
    private bool _settingsWasOpen;

    /// <summary>本次 Show 是否要保留轮盘原有位置（菜单重建时为真）。</summary>
    private (int X, int Y, int Width, int Height)? _preserveRadialPosition;

    /// <summary>
    /// 我们自己的窗口层级栈。定义一次，之后所有置顶重申都走它。
    ///
    /// 之前是每个窗口各自调一个 ReassertTopmost，而那些实现都是空操作
    /// （传了 SWP_NOZORDER，让插入位置被忽略）。集中定义还有一个好处：
    /// 层级关系变成一处可读、可验证的声明，而不是散在四个文件里的隐式约定。
    ///
    /// 从上到下声明；重申时自动反序执行，保证栈顶最后插入、确实在最前。
    /// </summary>
    private readonly ZOrderManager _zOrder = new();
    private readonly ForegroundFocus _focus = new();
    private readonly DispatcherTimer _topmostTimer;

    /// <summary>退场动画播完后才真正隐藏窗口。动画不影响动作执行，只影响"什么时候看不见"。</summary>
    private readonly DispatcherTimer _exitTimer;
    private HwndSource? _radialSource;

    private bool _disposed;

    public OverlayController(MenuSpec menu)
    {
        _radial = CreateRadialWindow(menu);

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TopmostReassertInterval,
        };
        // 定时器先做廉价检查，只在真的需要时才重申 —— 无条件重申会让分层窗口
        // 持续重新合成，表现为闪烁（用户报的现象）。
        _topmostTimer.Tick += (_, _) =>
        {
            if (_zOrder.NeedsReassert()) ReassertTopmost();
        };

        // 系统对话框必须在最顶层参与重申。
        //
        // 为什么不是"给对话框加个 topmost 就完事"：我们的 800ms 置顶重申会把
        // 自己的窗口逐个插到 topmost 层最前面，那样反而会把对话框挤到我们后面。
        // 让它成为层级栈的顶层，重申结束时它才确实在最前面。
        _zOrder.AddLayer("系统对话框", () => FindOwnedSystemDialog() is { } dialog ? [dialog] : []);

        // 层级从上到下声明。注意这里**必须**与视觉预期一致：
        // 设置面板盖住操作条，操作条盖住轮盘，轮盘盖住遮罩。
        _zOrder.AddLayer("设置面板", () => _settingsPanel is { IsVisible: true } ? [_settingsPanel.HandleValue] : []);
        _zOrder.AddLayer("操作条", () => _actionBar.IsVisible ? [_actionBar.HandleValue] : []);
        _zOrder.AddLayer("轮盘", () => _radial.IsVisible ? [_radial.HandleValue] : []);
        _zOrder.AddLayer("遮罩", () => _dims.Where(d => d.IsValid).Select(d => d.HandleValue).ToList());

        // 设置改为开在覆盖层会话里（面板与轮盘同时在场），所以这里不再收掉覆盖层 ——
        // 收掉的话"改动立刻看到效果"就无从谈起了。
        _actionBar.SettingsRequested += (_, _) =>
        {
            if (IsSettingsOpen) CloseSettings();
            else OpenSettings();
        };

        _actionBar.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _exitTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _exitTimer.Tick += (_, _) =>
        {
            _exitTimer.Stop();
            FinishHide();
        };
    }

    private RadialWindow CreateRadialWindow(MenuSpec menu)
    {
        var window = new RadialWindow(menu);
        window.DismissRequested += (_, _) => Hide();
        window.ItemActivated += (_, selection) => ItemActivated?.Invoke(this, selection);
        window.AddItemRequested += (_, parentIndex) => AddItemRequested?.Invoke(this, parentIndex);
        window.SectorEditRequested += (_, request) => SectorEditRequested?.Invoke(this, request);
        window.FocusLost += (_, _) => OnRadialFocusLost();
        return window;
    }

    /// <summary>
    /// 换用另一份菜单。
    ///
    /// **就地替换视觉树，不重建窗口。** 早先这里是"销毁窗口 + 新建 + Show"，
    /// 效果等于把整个覆盖层重开一次：轮盘消失、再播一遍入场动画长出来，
    /// 用户每改一次快捷选项都要看一次这个。
    ///
    /// 就地替换省掉的不只是动画：窗口句柄不变 → 不重新插入 topmost band、
    /// 不重新抢焦点、不必重新定位，也就没有"重建过程里顺序错了导致点不到"的机会。
    /// 位置保留不再需要显式处理 —— 窗口压根没动过。
    /// </summary>
    public void RebuildForMenu(MenuSpec menu)
    {
        if (_disposed) return;

        _radial.ApplyMenu(menu, editMode: IsSettingsOpen);
    }

    /// <summary>
    /// 弹「选一个程序加进菜单」的文件对话框；用户取消时返回 null。
    ///
    /// 对话框期间必须同时做两件事，缺任何一件都会出错：
    /// 提高置顶重申频率（对话框不会自动变成 topmost，不重申就会被遮罩盖住一闪），
    /// 以及屏蔽"失去焦点就关闭"（对话框夺走前台焦点，否则会顺手把覆盖层关掉）。
    /// </summary>
    public string? PickExecutableForMenu()
    {
        if (_disposed) return null;

        _modalDialogDepth++;
        SetTopmostInterval(FastTopmostReassertInterval);
        try
        {
            return ProgramPicker.Pick(_radial);
        }
        finally
        {
            _modalDialogDepth--;
            SetTopmostInterval(TopmostReassertInterval);
        }
    }

    /// <summary>同时打开的系统对话框层数（正常只可能是 0 或 1）。</summary>
    private int _modalDialogDepth;

    /// <summary>遮罩不透明度的用户覆盖值（0–1）。null 表示沿用设计基线。</summary>
    public double? DimOpacityOverride { get; private set; }

    /// <summary>
    /// 更新遮罩不透明度。已建好的遮罩窗口用的是旧的 alpha，必须重建才会生效。
    /// 只重建遮罩窗口，不动轮盘 —— 后者重建的代价大得多（要重画视觉树）。
    /// </summary>
    public void ApplyDimOpacity(double? opacity)
    {
        if (_disposed) return;
        if (DimOpacityOverride == opacity) return;

        DimOpacityOverride = opacity;

        // **就地更新 alpha，不重建窗口。**
        //
        // 之前这里是"销毁整屏遮罩窗口 + 重建"，每次滑动滑块都会让整个屏幕闪一下 ——
        // 用户报的"设置界面一直在闪烁"里就有这一份。
        // 改分层窗口的 alpha 只是个属性更新，系统重新合成一次即可，观感是连续变化的。
        var alpha = ToAlpha(opacity);
        foreach (var dim in _dims) dim.SetAlpha(alpha);
    }

    private static byte ToAlpha(double? opacity) =>
        opacity is { } value
            ? (byte)Math.Clamp(Math.Round(value * 255), 0, 255)
            : RawDimWindow.DefaultAlpha;

    public bool IsVisible { get; private set; }

    /// <summary>诊断用：遮罩窗口句柄。供 --overlay-probe 核对窗口真实状态，而不是相信自己的布尔量。</summary>
    internal IReadOnlyList<nint> DimHandles => _dims.Select(d => d.HandleValue).ToList();

    /// <summary>诊断用：轮盘窗口句柄。</summary>
    internal nint RadialHandle => _radial.HandleValue;

    /// <summary>诊断用：子弧是否展开。</summary>
    internal bool IsChildArcExpanded => _radial.IsChildArcVisible;

    /// <summary>诊断用：子弧展开进度。</summary>
    internal double ChildArcProgress => _radial.ChildArcProgress;

    /// <summary>诊断用：渲染出来的子项数量。</summary>
    internal int ChildItemCount => _radial.ChildItemCount;

    /// <summary>诊断用：视觉树上的直接子元素数量。</summary>
    internal int VisualChildCount => _radial.VisualChildCount;

    /// <summary>诊断用：跳过悬停延迟直接展开子弧。</summary>
    internal void ExpandChildArcForProbe(int parentIndex) => _radial.ExpandChildArc(parentIndex);

    /// <summary>诊断用：根环「＋」的索引。</summary>
    internal int AddSectorIndexForProbe => _radial.AddSectorIndex;

    /// <summary>诊断用：子弧「＋」的索引。</summary>
    internal int ChildAddSectorIndexForProbe => _radial.ChildAddSectorIndex;

    /// <summary>诊断用：按真实命中结果走一遍右键分派。</summary>
    internal WinQuick.Core.Geometry.HitResult RightClickForProbe(double x, double y) =>
        _radial.RightClickForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：编辑面板是否开着且真的在视觉树上。</summary>
    internal bool IsEditPanelOpen => _radial.IsEditPanelOpen;

    /// <summary>诊断用：直接要求编辑某一项，跳过命中测试。</summary>
    internal void ShowSectorEditForProbe(int rootIndex, int childIndex) =>
        _radial.ShowSectorEditForProbe(rootIndex, childIndex);

    /// <summary>诊断用：收起编辑面板。</summary>
    internal void DismissSectorEditForProbe() => _radial.DismissSectorEditForProbe();

    /// <summary>诊断用：直接切换编辑态（正式路径由打开/关闭设置面板驱动）。</summary>
    internal void SetEditModeForProbe(bool editMode) => _radial.SetEditMode(editMode);

    /// <summary>诊断用：把某个扇区置为悬停，制造一次进行中的形变动画。</summary>
    internal void HoverForProbe(int index) => _radial.HoverForProbe(index);

    /// <summary>诊断用：面板当前编辑的是哪一项。</summary>
    internal (int RootIndex, int ChildIndex) EditTargetForProbe => _radial.EditTargetForProbe;

    /// <summary>诊断用：走一遍 Esc 的处理路径（收面板 → 收子弧 → 关覆盖层）。</summary>
    internal void EscapeForProbe() => _radial.EscapeForProbe();

    /// <summary>诊断用：按真实命中结果走一遍左键分派（含"面板开着时先收面板"）。</summary>
    internal WinQuick.Core.Geometry.HitResult LeftClickForProbe(double x, double y) =>
        _radial.LeftClickForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：把窗口内坐标交给轮盘做命中测试（走的是点击同一套数学）。</summary>
    internal WinQuick.Core.Geometry.HitResult HitTestForProbe(double x, double y) =>
        _radial.HitTestForProbe(new System.Windows.Point(x, y));

    /// <summary>诊断用：按真实命中结果走一遍点击分派，事件照常发出。</summary>
    internal WinQuick.Core.Geometry.HitResult ClickForProbe(double x, double y) =>
        _radial.ClickForProbe(new System.Windows.Point(x, y));

    /// <summary>重建菜单后把某个父项的子弧重新展开，让用户看见刚加进去的项。</summary>
    public void ReopenChildArc(int parentIndex) => _radial.ReopenChildArc(parentIndex);

    internal void CollapseChildArcForProbe() => _radial.CollapseChildArc();

    /// <summary>
    /// 某个叶子项被点击。<c>ChildIndex</c> 为 -1 表示第一层的项，否则是子弧里的子项。
    /// </summary>
    public event EventHandler<(int ParentIndex, int ChildIndex)>? ItemActivated;

    /// <summary>
    /// 用户点了环里的「＋」，要求往那一级加一项。
    /// 参数是父项索引；-1 表示第一层。
    /// </summary>
    public event EventHandler<int>? AddItemRequested;

    /// <summary>用户在扇区的编辑面板上选了改名或移除。</summary>
    public event EventHandler<(int RootIndex, int ChildIndex, Radial.SectorEditAction Action, string? Label)>?
        SectorEditRequested;

    /// <summary>设置面板正在显示。</summary>
    public bool IsSettingsOpen => _settingsPanel is { IsVisible: true };

    /// <summary>设置面板里的改动需要被应用与持久化。参数是组装好的新配置。</summary>
    public event EventHandler<AppConfig>? SettingsChanged;

    /// <summary>设置面板已关闭（用户点 ✕ 或按 Esc）。</summary>
    public event EventHandler? SettingsClosed;

    /// <summary>设置面板里改了菜单（增删项、改名称）。需要写回 menus.json 并重建轮盘。</summary>

    /// <summary>用户在覆盖层左上角点了「退出」。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// 预先创建所有窗口并隐藏。必须在启动时调用一次，把渲染栈、字体缓存、
    /// D3D 表面都热起来 —— 否则第一次唤出会明显卡顿。
    /// </summary>
    public void Prewarm()
    {
        RebuildDimWindows();

        // 让轮盘窗口真正渲染一次再隐藏：只创建不显示的话，字体与渲染目标都还没分配，
        // 第一次唤出仍然会卡。
        //
        // ShowActivated 必须先关掉 —— 否则这个预热用的 Show 会在开机时把焦点从
        // 用户正在用的窗口抢走。位置也放到屏幕外，避免开机闪一下。
        _radial.ShowActivated = false;
        _radial.Left = -32000;
        _radial.Top = -32000;
        _radial.Show();
        _radial.Hide();
        _radial.ShowActivated = true;

        // 窗口句柄此时才存在，所以消息钩子只能在这里挂。
        // 显示器拓扑变化后遮罩窗口的尺寸全都会失效，必须重建窗口池。
        _radialSource = (HwndSource?)PresentationSource.FromVisual(_radial);
        _radialSource?.AddHook(OnRadialMessage);
    }

    /// <summary>
    /// 显示覆盖层。
    /// **必须同步完成** —— 前台权限的豁免只在同一次输入事件的调用栈内有效，
    /// 一旦 await、切线程或 PostMessage 之后再设置前台窗口，权限就丢了。
    /// </summary>
    public void Show() => Show(null);

    /// <param name="preserveRadialPosition">
    /// 非 null 时把轮盘摆在给定的物理像素矩形，而不是光标处。
    /// 供"菜单变更后重建"使用 —— 那时用户的操作焦点在设置面板上，
    /// 把轮盘挪到光标处会正好压住面板。
    /// </param>
    private void Show((int X, int Y, int Width, int Height)? preserveRadialPosition)
    {
        if (_disposed || IsVisible) return;

        _preserveRadialPosition = preserveRadialPosition;

        // 停掉可能还在跑的退场计时器 —— 否则它会在 167ms 后把刚显示出来的轮盘又藏回去。
        // （ApplyDimOpacity 与菜单重建都会在 Hide 之后紧接着 Show，这条路径很常见。）
        _exitTimer.Stop();

        // 显示器数量变了就重建遮罩窗口池，否则会有屏没被遮住。
        if (_dims.Count != MonitorLayout.All().Count)
            RebuildDimWindows();

        _focus.Save();

        foreach (var dim in _dims)
        {
            dim.ReassertTopmost();
            dim.Show();
        }

        // 先摆位置再让 WPF 显示 —— 顺序反了会先在旧位置闪一帧。
        if (_preserveRadialPosition is { } bounds) _radial.PlaceAt(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        else _radial.PlaceAtCursor();

        _radial.Show();
        _radial.ReassertTopmost();
        _radial.PlayEntrance();

        _actionBar.PlaceAtMonitorCorner();
        _actionBar.Show();
        _actionBar.ReassertTopmost();
        _radial.StartInteraction();

        // 拿键盘焦点，否则 Esc 收不到。这里同步调用是刻意的（见方法注释）。
        // 设置面板开着时不能夺焦点 —— 用户可能正在里面打字（热键录制、改名、填色值），
        // 抢走焦点会打断输入。
        if (!IsSettingsOpen) ForegroundFocus.AttachTo(_radial);

        // 恢复显隐往返之前打开着的设置面板。
        if (_settingsWasOpen && _settingsPanel is not null)
        {
            _settingsPanel.PlaceAtMonitorCornerIfNeeded();
            PositionSettingsPanel();
            _settingsPanel.Show();
            _settingsWasOpen = false;
            ForegroundFocus.AttachTo(_settingsPanel);
        }

        // 编辑态对齐到"面板是不是开着"。**必须放在上面恢复面板之后** ——
        // 放在之前算出来的 IsSettingsOpen 是过时的（Hide 时把面板藏了），
        // 结果就是"面板是开着的，轮盘上却没有「＋」"。
        _radial.SetEditMode(IsSettingsOpen);

        IsVisible = true;
        _topmostTimer.Start();
    }

    /// <summary>
    /// 隐藏覆盖层并把前台焦点还给用户原来的窗口。
    ///
    /// 遮罩层立刻消失（它是用户的视觉反馈），轮盘窗口等退场动画播完再隐藏。
    /// 焦点**立刻**归还 —— 用户按下扇区之后应马上回到原来的窗口，
    /// 不能等动画放完才还（Kando #650 的教训）。
    /// </summary>
    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;

        _topmostTimer.Stop();
        // 指针轮询必须停 —— 它每 25ms 唤醒一次 UI 线程，窗口不可见时纯属浪费。
        _radial.StopInteraction();
        foreach (var dim in _dims) dim.Hide();
        _actionBar.Hide();
        // 记住面板状态：显隐往返（改设置、重建菜单、显示器变化）之后要恢复它，
        // 否则用户正在填的表单会毫无预兆地消失。
        _settingsWasOpen = _settingsPanel is { IsVisible: true };
        _settingsPanel?.Hide();

        // 退场期间不再接受输入，否则用户在收缩过程中还能点到扇区。
        _radial.IsHitTestVisible = false;

        var exitMs = TokenStore.Current.Motion.ExitDurationMs;
        if (AnimationsEnabled() && exitMs > 0)
        {
            _radial.PlayExit();
            _exitTimer.Interval = TimeSpan.FromMilliseconds(exitMs + 20);
            _exitTimer.Start();
        }
        else
        {
            FinishHide();
        }

        // 焦点立刻归还，不等动画。
        _focus.Restore();
    }

    /// <summary>
    /// 轮盘失去焦点。
    ///
    /// 只有当焦点落到了**别人的窗口**上才关闭。焦点若落在我们自己的窗口上
    /// （左上角操作条就是这种情况），说明用户仍在操作覆盖层，不该关。
    /// 不判这一下的话，点操作条上的按钮会在 Click 完成前就把覆盖层隐藏掉，
    /// 表现是"按钮点了没反应、只是关闭" —— 这正是用户反馈的现象。
    /// </summary>
    private void OnRadialFocusLost()
    {
        if (!IsVisible) return;

        // 系统对话框正在弹出/收起的这一段里，"前台窗口不是我们"是预期状态而不是用户离开。
        // 少了这一条，用户点「＋」选程序的过程中轮盘就被自己关掉了 ——
        // 选完文件回来发现覆盖层没了，而"再加一项"这件事还没做完。
        if (_modalDialogDepth > 0) return;

        var foreground = PInvoke.GetForegroundWindow();
        if (IsOwnWindow(foreground)) return;

        Hide();
    }

    private bool IsOwnWindow(HWND hwnd)
    {
        if (hwnd == default) return false;
        if (hwnd == (HWND)_radial.HandleValue) return true;
        if (hwnd == (HWND)_actionBar.HandleValue) return true;
        if (_settingsPanel is not null && hwnd == (HWND)_settingsPanel.HandleValue) return true;
        return _dims.Any(d => hwnd == (HWND)d.HandleValue);
    }

    /// <summary>
    /// 打开设置面板。面板与轮盘同时在场，所以拖滑块、改高亮色都能立刻在轮盘上看到效果。
    /// </summary>
    public void OpenSettings()
    {
        if (_disposed || !IsVisible) return;
        if (IsSettingsOpen) return;

        _settingsPanel ??= CreateSettingsPanel();

        // 两遍定位：先按"最大高度"估个位置，让窗口出现时就大致在正确的地方（避免可见的跳动）；
        // 显示并完成布局后，再按**真实尺寸**重新摆一次并钳制在屏内。
        //
        // 为什么必须两遍：布局完成前 ActualHeight 不可靠，按它算出的 top 会让面板底部溢出屏幕，
        // 而溢出的恰恰是最常用的底部按钮 —— 用户就点不到了。
        // （这个溢出是被探针的「面板完整落在屏内」核对抓到的。）
        PositionSettingsPanel();
        _settingsPanel.Show();
        _settingsPanel.UpdateLayout();
        PositionSettingsPanel();

        // 进设置态才把「＋」扇区放出来：日常使用时轮盘里只该有用户自己的项。
        // 面板是开在覆盖层旁边的，所以"打开面板 → 轮盘上出现「＋」"是同一个视野里的变化。
        _radial.SetEditMode(true);

        _settingsPanel.PlayEntrance();

        // 面板需要键盘输入（热键录制、文本框），所以它会夺走轮盘的焦点。
        // 这不影响覆盖层 —— OnRadialFocusLost 会认出"焦点落在自己人身上"。
        ForegroundFocus.AttachTo(_settingsPanel);
    }

    /// <summary>关闭设置面板，把焦点还给轮盘（否则 Esc 就不再关闭覆盖层了）。</summary>
    public void CloseSettings()
    {
        if (_settingsPanel is not { IsVisible: true }) return;

        _settingsPanel.Hide();

        // 退出设置态就收回「＋」，同时把子弧重新展开（索引没变，用户看到的是同一份菜单）。
        _radial.SetEditMode(false);
        ForegroundFocus.AttachTo(_radial);
        SettingsClosed?.Invoke(this, EventArgs.Empty);
    }

    private SettingsPanel CreateSettingsPanel()
    {
        var panel = new SettingsPanel(
            _settings,
            probeHotkey: spec => _settingsPanelProbe(spec),
            applyConfig: config => SettingsChanged?.Invoke(this, config));

        panel.CloseRequested += (_, _) => CloseSettings();

        return panel;
    }

    /// <summary>由常驻装配注入的热键探测（会临时释放当前热键，见 HotkeyManager.ProbeTemporarily）。</summary>
    private Func<Core.Hotkey.HotkeySpec, (bool Available, int ErrorCode)>? _settingsPanelProbe;

    /// <summary>设置面板显示时所用的当前配置。</summary>
    private AppConfig _settings = new();

    /// <summary>告诉覆盖层"面板打开时应当以哪份配置为基准"。</summary>
    public void SetSettingsSource(AppConfig config) => _settings = config;

    /// <summary>注入热键探测函数。</summary>
    public void SetHotkeyProbe(Func<Core.Hotkey.HotkeySpec, (bool Available, int ErrorCode)> probe) =>
        _settingsPanelProbe = spec => probe(spec);

    /// <summary>
    /// 把设置面板摆在光标所在屏的**另一侧**，尽量避免压住轮盘
    /// （轮盘跟着光标走，所以把面板放到光标的反方向最不容易重叠）。
    /// </summary>
    private void PositionSettingsPanel()
    {
        if (_settingsPanel is null) return;

        var monitor = MonitorLayout.AtCursor();
        var scale = monitor.Scale;

        const int margin = 24;

        // 先把最大高度限制在屏幕内：面板在小屏幕上可能比屏幕还高，
        // 那时底部的按钮会被截掉、点不到。内容本身可滚动（见 XAML 的 ScrollViewer）。
        var availableDips = (monitor.Height - margin * 2) / scale;
        if (_settingsPanel.MaxHeight <= 0 || _settingsPanel.MaxHeight > availableDips)
            _settingsPanel.MaxHeight = Math.Max(320, availableDips);

        var panelWidth = (int)Math.Round(_settingsPanel.Width * scale);

        // 布局完成前 ActualHeight 不可靠，退化为按最大高度估算 ——
        // 第一遍（Show 之前）走的就是这条。
        var measuredDips = _settingsPanel.ActualHeight;
        var panelHeight = measuredDips > 0
            ? (int)Math.Round(measuredDips * scale)
            : (int)Math.Round(_settingsPanel.MaxHeight * scale);

        var cursorOnLeftHalf = !MonitorLayout.TryGetCursorPosition(out var cx, out _)
                               || cx < monitor.X + monitor.Width / 2;

        var left = cursorOnLeftHalf
            ? monitor.Right - panelWidth - (int)Math.Round(margin * scale)
            : monitor.X + (int)Math.Round(margin * scale);

        var top = monitor.Y + (monitor.Height - panelHeight) / 2;

        // 钳制在屏内。竖直方向尤其重要：面板是纵向表单，底部是最常用的按钮，
        // 溢出屏幕就等于点不到 —— 用户已经因为"按钮点不到"报过一次问题了。
        left = Math.Clamp(left, monitor.X, Math.Max(monitor.X, monitor.Right - panelWidth));
        top = Math.Clamp(top, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - panelHeight));

        var hwnd = (HWND)_settingsPanel.HandleValue;
        PInvoke.SetWindowPos(hwnd, default, left, top, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// 实时更新随设置变化的外观项。改动立刻生效，不等保存。
    /// </summary>
    public void ApplyRuntimeStyle(AppConfig config)
    {
        ApplyDimOpacity(config.Appearance.DimOpacity);
        _radial.ApplyAccent(config.Appearance.AccentColor);
        _radial.ApplyHoverDelays(config.Behavior.HoverOpenDelayMs, config.Behavior.HoverCloseDelayMs);
        _radial.ApplyAnimations(config.Behavior.AnimationsEnabled);
    }

    /// <summary>诊断用：操作条句柄。</summary>

    internal nint ActionBarHandle => _actionBar.HandleValue;

    /// <summary>诊断用：操作条是否可见。</summary>
    internal bool IsActionBarVisible => _actionBar.IsVisible;

    /// <summary>诊断用：设置面板句柄。</summary>
    internal nint SettingsPanelHandle => _settingsPanel?.HandleValue ?? 0;

    /// <summary>
    /// 开始监听前台变更事件（事件驱动地重申置顶）。
    /// 必须在显示覆盖层之前调用一次；与定时器互补 ——
    /// 定时器只兜「置顶位被清 / 内部顺序被重建打乱」，事件兜「别人抢到前台」。
    /// </summary>
    public void StartZOrderWatching() => _zOrder.WatchForeground();

    /// <summary>诊断用：轮盘当前的物理像素矩形。</summary>
    internal (int X, int Y, int Width, int Height)? RadialPhysicalBounds => _radial.CurrentPhysicalBounds;

    /// <summary>诊断用：z 序管理器（探针用它读取重申次数）。</summary>
    internal ZOrderManager ZOrder => _zOrder;

    /// <summary>诊断用：设置面板实例（探针需要它来触发对话框）。</summary>
    internal SettingsPanel? SettingsPanelForProbe => _settingsPanel;

    /// <summary>诊断用：操作条「设置」按钮中心的屏幕物理像素坐标。</summary>
    internal (int X, int Y)? ActionBarSettingsButtonScreenPoint() => ActionBarSettingsButtonCenterInScreen();

    /// <summary>诊断用：「设置」按钮中心在屏幕上的物理像素坐标。</summary>
    internal (int X, int Y)? ActionBarSettingsButtonCenterInScreen()
    {
        if (_actionBar.SettingsButtonCenter is not { } center) return null;

        var handle = (HWND)_actionBar.HandleValue;
        if (!PInvoke.GetWindowRect(handle, out var rect)) return null;

        var scale = MonitorLayout.AtCursor().Scale;
        return (rect.left + (int)Math.Round(center.X * scale),
                rect.top + (int)Math.Round(center.Y * scale));
    }

    /// <summary>真正把轮盘窗口隐藏并重置动画状态。</summary>
    private void FinishHide()
    {
        // 期间若已被重新显示（Hide 之后紧接着 Show），这次的收尾就作废。
        if (IsVisible) return;

        _radial.Hide();
        _radial.ResetAnimationState();
        _radial.ResetInteraction();
        _radial.IsHitTestVisible = true;
    }

    private static bool AnimationsEnabled()
    {
        try { return SystemParameters.ClientAreaAnimation; }
        catch { return true; }
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    /// <summary>显示器变更、会话解锁等场景下的强制收尾 —— 宁可多关一次，也不要留下残影。</summary>
    public void ForceHideAndRebuild()
    {
        // 这条路径**不等动画**，直接立刻隐藏：异常场景下确定性比观感重要。
        _exitTimer.Stop();

        if (IsVisible)
        {
            IsVisible = false;
            _topmostTimer.Stop();
            _focus.Restore();
        }

        FinishHide();
        foreach (var dim in _dims) dim.Hide();
        RebuildDimWindows();
    }

    private void RebuildDimWindows()
    {
        foreach (var dim in _dims)
        {
            // 事件解绑后再销毁，避免已销毁的窗口回调打到活着的控制器上。
            dim.DismissRequested -= OnDimDismiss;
            dim.Dispose();
        }
        _dims.Clear();

        // 每屏一个遮罩窗口。一个 HWND 只能有一个 DPI，所以不能合并成一个横跨的窗口。
        // 遮罩用统一 alpha（40% 黑），原生窗口实测几乎零内存开销。
        // 透明度可以由用户配置覆盖；默认用设计基线里的值。
        var alpha = ToAlpha(DimOpacityOverride);

        foreach (var monitor in MonitorLayout.All())
        {
            var dim = new RawDimWindow(alpha);
            dim.SetPhysicalBounds(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            dim.DismissRequested += OnDimDismiss;
            _dims.Add(dim);
        }

        // 重建之后必须立刻重申 z 序。
        //
        // 这是本轮那个 bug 的发生点：新建的窗口会被插到 topmost 层**最前面**，
        // 于是重建出来的遮罩压在轮盘与设置面板之上 —— 它们仍然可见（遮罩只有 40% 不透明），
        // 但点击会被遮罩全部吃掉，用户看到的就是「看得见却点不动」。
        // 而这个重建每次改设置都会走（拖遮罩深浅 → 保存 → 热重载 → 重建）。
        ReassertTopmost();
    }

    private void OnDimDismiss()
    {
        // 点在遮罩上 = 点在轮盘之外的空白处。
        Hide();
    }

    /// <summary>
    /// 按层级栈重申整个 z 序，从下往上插入，保证结果确定等于栈的顺序。
    /// </summary>
    private void ReassertTopmost() => _zOrder.Reassert();

    /// <summary>
    /// 找出当前由我们某个窗口拥有的系统对话框（文件选择、消息框等）。
    ///
    /// 判据用**owner 关系**而不是窗口类名：`ShowDialog(owner)` 会把 owner 设成调用方，
    /// 而窗口类名 `#32770` 是通用对话框类，全系统都在用 —— 按类名判会误伤别人的对话框。
    ///
    /// 找到之后要**主动**把它置顶：走的是系统对话框自己的窗口，我们改不了它的创建参数，
    /// 只能事后补样式。这也是为什么必须由重申逻辑兜着 —— 补一次不够，
    /// 我们自己的窗口每次重申都可能把它挤下去。
    /// </summary>
    private nint FindOwnedSystemDialog()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground == default) return 0;
        if (IsOwnWindow(foreground)) return 0;
        if (!PInvoke.IsWindowVisible(foreground)) return 0;

        if (!IsOwnedByUs(foreground)) return 0;

        // 补上 topmost：对话框创建时并不知道我们的窗口是置顶的，
        // 而官方语义是「一个窗口**变成** topmost 时其被拥有窗口才连带变 topmost」——
        // 我们的面板早就是 topmost 了，这个转变从未发生，所以对话框一直是非置顶的。
        ZOrderManager.BringToTopmostFront(foreground);
        return (IntPtr)foreground;
    }

    /// <summary>这个窗口是不是由我们的某个窗口拥有的。</summary>
    private bool IsOwnedByUs(HWND hwnd)
    {
        var owner = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER);
        if (owner == default) return false;

        return IsOwnWindow(owner);
    }

    private IntPtr OnRadialMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_* 在 CsWin32 里是 uint，而消息参数是 int，所以这里转成 uint 再比对。
        switch ((uint)msg)
        {
            case PInvoke.WM_DISPLAYCHANGE:
                // 退出全屏游戏、插拔显示器、改分辨率都会走到这里。
                Hide();
                RebuildDimWindows();
                break;

            case PInvoke.WM_KILLFOCUS:
                // 窗口的 Deactivated 事件覆盖不到所有路径，这里作为兜底。
                // 同样要判"是不是自己人抢的焦点"。
                OnRadialFocusLost();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _topmostTimer.Stop();
        _exitTimer.Stop();
        _zOrder.StopWatching();
        _radialSource?.RemoveHook(OnRadialMessage);

        foreach (var dim in _dims)
        {
            dim.DismissRequested -= OnDimDismiss;
            dim.Dispose();
        }
        _dims.Clear();

        _radial.Close();
        _actionBar.Close();
        _settingsPanel?.Close();
    }
}
