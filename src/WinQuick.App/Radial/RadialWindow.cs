using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using WinQuick.Core.Interaction;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Radial;

/// <summary>
/// 轮盘层窗口 —— 整个覆盖层里唯一使用 WPF 逐像素透明的窗口。
///
/// 为什么这里可以用昂贵的逐像素透明：窗口尺寸被压到 740 DIP（约全屏的 1/3 面积），
/// 且更新只在悬停切换与入场/退场动画期间才频繁。相比之下遮罩层是整个屏幕、
/// 必须用原生 LWA_ALPHA 窗口（见 RawDimWindow 注释与 docs/M0-findings.md 实测）。
///
/// 定位用 SetWindowPos 传物理像素，不走 WPF 的 Left/Top —— 后者是 DIP，
/// 在 175% 缩放这类非整数倍率下与物理像素混算必然错位。
/// </summary>
public sealed class RadialWindow : Window
{
    private readonly DesignTokens _tokens;
    private readonly ScaleTransform _scale = new(1, 1);

    /// <summary>当前视觉树。换菜单/切编辑态时**就地替换它**，窗口本身不动。</summary>
    private RadialRingView _view;

    /// <summary>用户改过的高亮色。换视觉树时要重新应用 —— 它存在视图上，不在窗口上。</summary>
    private string? _accentHex;

    private bool _editMode;

    /// <summary>当前这份菜单。就地换树时要用它作为"菜单没变、只切编辑态"的那一份。</summary>
    private MenuSpec _menu;
    private readonly SubmenuHoverMachine _hoverMachine;
    private readonly DispatcherTimer _pointerPoll;
    private Point? _lastPointer;
    private bool _stylesApplied;

    public RadialWindow(MenuSpec menu)
    {
        _tokens = TokenStore.Current;

        // 窗口级操作计数（--flicker-probe）：必须在这里挂上，晚一步就漏掉前面几次。
        SurfaceOps.Watch(this, SurfaceOwner.Wheel);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;          // 逐像素 alpha；圆盘外必须真透明
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;               // 需要键盘焦点才能响应 Esc

        // DIP 尺寸恒定；实际位置与物理尺寸由 SetWindowPos 决定。
        Width = _tokens.Radius.WindowSize;
        Height = _tokens.Radius.WindowSize;

        _menu = menu;
        _view = RadialRingView.Build(menu, editMode: _editMode);
        Content = _view.Root;

        // 入场缩放挂在以圆心为原点的 ScaleTransform 上。
        // RenderTransformOrigin 必须是 0.5,0.5，否则圆盘会从左上角放大。
        _view.Root.RenderTransformOrigin = new Point(0.5, 0.5);
        _view.Root.RenderTransform = _scale;

        _hoverMachine = new SubmenuHoverMachine(_tokens.Interaction);

        // 状态机必须被**反复**喂入当前指针位置，而不是只在指针移动时喂。
        // 原因是「等待展开延迟」这件事需要靠时间推进来触发：指针停住不动时，
        // 若只在移动时才检查，子菜单就永远不会展开。（这条已用单测锁住。）
        _pointerPoll = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _pointerPoll.Tick += (_, _) =>
        {
            CheckLongPress();
            EvaluateHover();
        };

        WireViewEvents(_view);

        SourceInitialized += (_, _) => ApplyExtendedStyles();

        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseLeftButtonUp += OnMouseLeftUp;
        MouseRightButtonDown += OnMouseRightDown;
        KeyDown += OnKeyDown;
        // 失去焦点时不直接关闭，而是交给覆盖层判断：新前台窗口如果是我们自己的窗口
        // （比如左上角操作条），就不该关 —— 用户是在用覆盖层，不是在离开它。
        Deactivated += (_, _) => FocusLost?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>要求关闭覆盖层（Esc / 点圆盘外）。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>轮盘失去键盘焦点。是否真的关闭由覆盖层决定。</summary>
    public event EventHandler? FocusLost;

    /// <summary>
    /// 某个叶子项被点击选中。
    /// <paramref name="parentIndex"/> 是第一层扇区索引；
    /// <paramref name="childIndex"/> 为 -1 表示选中的是第一层本身，否则是子弧里的子项。
    /// </summary>
    public event EventHandler<(int ParentIndex, int ChildIndex)>? ItemActivated;

    /// <summary>
    /// 点在环里的「＋」上，要求往那一级加一项。
    /// <paramref name="parentIndex"/> 为 -1 表示加进第一层，否则加进该父项的子项列表。
    ///
    /// 由上层弹文件对话框并写盘 —— 窗口只负责"用户点了哪个位置的加号"这件事，
    /// 不知道菜单存在哪、也不碰配置文件。
    /// </summary>
    public event EventHandler<int>? AddItemRequested;

    /// <summary>
    /// 用户要求新建分组。参数是（父项索引, 分组名称）。
    /// 上层要再问一次"第一项是什么" —— 分组不能是空的。
    /// </summary>
    public event EventHandler<(int ParentIndex, string Label)>? AddGroupRequested;

    /// <summary>
    /// 用户拖拽重排了某一层。参数是（父项索引，从哪，到哪）；父项索引为 -1 表示第一层。
    /// </summary>
    public event EventHandler<(int ParentIndex, int From, int To)>? ReorderRequested;

    /// <summary>
    /// 用户在扇区的编辑面板上选了动作（改名 / 移除）。
    /// 参数是（第一层索引, 子项索引或 -1, 动作, 新名称）。
    /// </summary>
    public event EventHandler<(int RootIndex, int ChildIndex, SectorEditAction Action, string? Label)>?
        SectorEditRequested;

    public int HoverIndex => _view.HoverIndex;
    public int RenderedIconCount => _view.RenderedIconCount;

    /// <summary>诊断用句柄值。</summary>
    internal nint HandleValue => new WindowInteropHelper(this).Handle;

    private void ApplyExtendedStyles()
    {
        if (_stylesApplied) return;
        _stylesApplied = true;

        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

        // WS_EX_TOOLWINDOW 是唯一能可靠把窗口排除出 Alt+Tab 的样式。
        // 这里刻意**不**加 WS_EX_NOACTIVATE：轮盘需要键盘焦点（Esc、方向键），
        // 这与「不抢焦点」天然冲突，取舍是选择激活 + 关闭时还原前台窗口。
        ex |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)(uint)ex);
    }

    /// <summary>
    /// 把窗口摆到光标处（物理像素计算，整体钳制在目标屏内），但**不改变可见性**。
    ///
    /// 可见性必须由 WPF 的 Show/Hide 控制，不能在这里用 SetWindowPos 的
    /// SWP_SHOWWINDOW 直接显示 —— 那样 WPF 内部仍认为窗口是隐藏的，
    /// 之后的 Hide() 就成了空操作，窗口会留在屏幕上。
    /// （这个 bug 是被 --overlay-probe 查窗口真实状态抓到的。）
    /// </summary>
    /// <summary>当前窗口的物理像素矩形，供"重建时保留位置"使用。</summary>
    public (int X, int Y, int Width, int Height)? CurrentPhysicalBounds
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return null;
            if (!PInvoke.GetWindowRect((HWND)hwnd, out var rect)) return null;
            return (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }
    }

    /// <summary>
    /// 按给定的物理像素矩形摆放，不改变可见性。
    /// 重建窗口时用它保留原位 —— 否则轮盘会跳到光标处，正好压住用户正在操作的面板。
    /// </summary>
    public void PlaceAt(int x, int y, int width, int height)
    {
        ApplyExtendedStyles();

        var monitor = MonitorLayout.AtCursor();
        Left = x / monitor.Scale;
        Top = y / monitor.Scale;

        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        PInvoke.SetWindowPos(hwnd, default, x, y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

        _view.SetHover(-1);
    }

    public void PlaceAtCursor()
    {
        ApplyExtendedStyles();

        if (!MonitorLayout.TryGetCursorPosition(out var cursorX, out var cursorY))
        {
            var fallback = MonitorLayout.AtCursor();
            cursorX = fallback.X + fallback.Width / 2;
            cursorY = fallback.Y + fallback.Height / 2;
        }

        var monitor = MonitorLayout.AtCursor();
        var physicalSize = (int)Math.Round(_tokens.Radius.WindowSize * monitor.Scale);
        var (left, top) = MonitorLayout.ClampToMonitor(cursorX, cursorY, physicalSize, monitor);

        // 先把 DIP 坐标当初始提示，减少 WPF Show 时的位置跳变；
        // 随后用 SetWindowPos 以物理像素做权威定位，修正 DIP 换算的舍入误差。
        Left = left / monitor.Scale;
        Top = top / monitor.Scale;

        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        PInvoke.SetWindowPos(hwnd, default, left, top, physicalSize, physicalSize,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

        _view.SetHover(-1);
    }

    /// <summary>
    /// 换成另一份菜单，或切换编辑态：**就地替换视觉树，窗口本身不重建**。
    ///
    /// 之前这里走的是"销毁窗口 + 新建 + Show"，也就是把整个覆盖层重开一次 ——
    /// 用户看到的是轮盘消失、再播一遍入场动画长出来。改一项菜单的代价不该是重开。
    /// 就地换视觉树的差别很实在：窗口句柄不变（不重新插入 topmost band、
    /// 不重新抢焦点、不必重新定位），也没有入场动画要重播。
    ///
    /// 子弧按需保留：只有在开关编辑态、或增删子项这类**不改变项数**的变动后才重新展开。
    /// 增删第一层的项会让所有索引整体移位，那时继续按旧索引展开会张开一个
    /// 用户没在看的子菜单，不如收起来。
    /// </summary>
    public void ApplyMenu(MenuSpec menu, bool? editMode = null)
    {
        var previousItemCount = _view.MenuItemCount;
        var previousOpenParent = _view.ChildArcParent;

        if (editMode is { } mode) _editMode = mode;
        _menu = menu;

        // **丢树之前必须先把旧树上的逐帧回调摘掉。**
        // CompositionTarget.Rendering 是静态事件，残留的订阅会让整个旧视觉树
        // 既不被回收、又每帧都在跑 —— 而换菜单恰恰发生在用户正在操作轮盘的时候，
        // 形变/子弧动画很可能正在播。这条由 --add-sector-probe 的回调计数核对锁住。
        // **丢树之前必须先把旧树上的逐帧回调摘掉。**
        // CompositionTarget.Rendering 是静态事件，残留的订阅会让整个旧视觉树
        // 既不被回收、又每帧都在跑 —— 而换菜单恰恰发生在用户正在操作轮盘的时候，
        // 形变/子弧动画很可能正在播。这条由 --add-sector-probe 的回调计数核对锁住
        // （把这一行删掉，那条核对就会红）。
        _view.ResetInteraction();

        // 复用当前画布：换掉 Window.Content 会让整棵内容失效、走一次窗口级布局，
        // 在分层窗口上表现为闪一下（改一次菜单就闪一次）。
        var fresh = RadialRingView.Build(menu, editMode: _editMode, reuse: Content as Canvas);

        // 先把动画状态归零再挂新树：退场动画的 HoldEnd 值会留在 _scale 上，
        // 不清掉的话新内容会从"上次退场结束时的缩放"开始。
        ResetAnimationState();

        fresh.Root.RenderTransformOrigin = new Point(0.5, 0.5);
        fresh.Root.RenderTransform = _scale;

        UnwireViewEvents(_view);
        _view = fresh;
        WireViewEvents(_view);

        // 换内容对象会让整棵内容失效、走一次窗口级布局 —— 分层窗口上就是闪一下。
        // 复用画布时 Root 与当前 Content 是同一个实例（等值 SetValue 是空操作），
        // 但这一点要**数出来**而不是"应该没问题"：计数不为 0 就说明复用没生效。
        SurfaceOps.MenuRebuild(swapped: !ReferenceEquals(Content, _view.Root));
        Content = _view.Root;
        _view.SetAccentOverride(_accentHex);

        // 状态机里存的是旧索引，换树后一律重置 —— 索引可能已经移位。
        _hoverMachine.Reset();
        _lastPointer = null;

        if (previousOpenParent >= 0
            && previousItemCount == _view.MenuItemCount
            && _view.RootItemHasChildren(previousOpenParent))
        {
            ReopenChildArc(previousOpenParent);
        }
    }

    /// <summary>
    /// 进入/退出编辑态（决定了环末尾画不画「＋」扇区）。
    ///
    /// 状态没变时直接返回：每次 Show 都会调它来对齐状态，不判一下就变成
    /// "每按一次热键都重建一遍视觉树" —— 白白重置悬停状态，还多付一次建树成本。
    /// </summary>
    public void SetEditMode(bool editMode)
    {
        if (_editMode == editMode) return;
        ApplyMenu(_menu, editMode);
    }

    /// <summary>
    /// 把视觉树上的上报事件接到窗口上。
    ///
    /// 换视觉树（<see cref="ApplyMenu"/>）时必须先摘后接：只接不摘的话，
    /// 旧的视觉树会一直被事件引用住，它的整棵元素树也就跟着留在内存里。
    /// </summary>
    private void WireViewEvents(RadialRingView view)
    {
        view.AddItemRequested += OnViewAddItemRequested;
        view.AddGroupRequested += OnViewAddGroupRequested;
        view.SectorEditRequested += OnSectorEditRequested;
    }

    private void UnwireViewEvents(RadialRingView view)
    {
        view.AddItemRequested -= OnViewAddItemRequested;
        view.AddGroupRequested -= OnViewAddGroupRequested;
        view.SectorEditRequested -= OnSectorEditRequested;
    }

    private void OnViewAddItemRequested(object? sender, int parentIndex) =>
        AddItemRequested?.Invoke(this, parentIndex);

    private void OnViewAddGroupRequested(object? sender, (int ParentIndex, string Label) request) =>
        AddGroupRequested?.Invoke(this, request);

    private void OnSectorEditRequested(
        object? sender, (int RootIndex, int ChildIndex, SectorEditAction Action, string? Label) request) =>
        SectorEditRequested?.Invoke(this, request);

    /// <summary>实时更新高亮色。设置面板改色后立刻可见。</summary>
    public void ApplyAccent(string? hex)
    {
        _accentHex = hex;
        _view.SetAccentOverride(hex);
    }

    /// <summary>实时更新悬停延迟。</summary>
    public void ApplyHoverDelays(int? openMs, int? closeMs) =>
        _hoverMachine.UpdateDelays(openMs, closeMs);

    /// <summary>
    /// 实时更新动画开关。null = 跟随系统「在 Windows 中显示动画」的无障碍设置。
    /// </summary>
    public void ApplyAnimations(bool? enabled) => _animationsOverride = enabled;

    private bool? _animationsOverride;

    /// <summary>诊断用：子弧是否可见。</summary>
    internal bool IsChildArcVisible => _view.IsChildArcVisible;

    internal double ChildArcProgress => _view.ChildArcProgress;

    internal int ChildItemCount => _view.ChildItemCount;

    internal int VisualChildCount => _view.VisualChildCount;

    /// <summary>
    /// 立刻重新展开某个父项的子弧（无动画），并把状态机同步过去。
    ///
    /// 用在"往子环里加了一项、菜单重建之后"：不这么做的话用户加完一项，
    /// 子环是收起的，他会以为"加进去的东西不见了"，得重新悬停一次才能确认。
    /// </summary>
    public void ReopenChildArc(int parentIndex)
    {
        if (!_view.RootItemHasChildren(parentIndex)) return;

        _hoverMachine.OpenImmediately(parentIndex);
        _view.ExpandChildArc(parentIndex, immediate: true);
        _view.SetHover(parentIndex);
    }

    /// <summary>诊断用：跳过悬停延迟直接展开子弧。</summary>
    internal void ExpandChildArc(int parentIndex) => _view.ExpandChildArc(parentIndex);

    /// <summary>诊断用：根环「＋」的索引。</summary>
    internal int AddSectorIndex => _view.AddSectorIndex;

    /// <summary>诊断用：某个标签的排版快照。</summary>
    internal (double Left, double FontSize, string Weight, bool Wrapping, double Width)
        LabelLayoutSnapshot(int index) => _view.LabelLayoutSnapshot(index);

    /// <summary>诊断用：某个标签是否挂着动画时钟。</summary>
    internal bool LabelHasAnimation(int index) => _view.LabelHasAnimation(index);

    /// <summary>诊断用：某个标签的完整文字。</summary>
    internal string LabelText(int index) => _view.LabelText(index);

    /// <summary>诊断用：中心的 "Esc" 提示是否可见。</summary>
    internal bool EscHintVisible => _view.EscHintVisible;

    /// <summary>诊断用：某个标签是否被截断。</summary>
    internal bool LabelIsElided(int index) => _view.LabelIsElided(index);

    /// <summary>诊断用：被悄悄裁掉（既没装下、又没标出截断）的标签。</summary>
    internal IReadOnlyList<string> SilentlyClippedLabels => _view.SilentlyClippedLabels;

    /// <summary>诊断用：某个标签的最终字号。</summary>
    internal double LabelFontSize(int index) => _view.LabelFontSize(index);

    /// <summary>诊断用：某个标签的文字占宽。</summary>
    internal double LabelTextWidth(int index) => _view.LabelTextWidth(index);

    /// <summary>诊断用：被截断的标签数。</summary>
    internal int ElidedLabelCount => _view.ElidedLabelCount;

    /// <summary>诊断用：最大标签溢出像素。</summary>
    internal double MaxLabelOverflow => _view.MaxLabelOverflow;

    /// <summary>诊断用：把某个扇区立即置为悬停（不播放动画）。</summary>
    internal void SetHoverImmediateForProbe(int index) => _view.SetHoverImmediate(index);

    /// <summary>诊断用：被悬停子项当前的鼓出像素数。</summary>
    internal double CurrentChildBulgeForProbe => _view.CurrentChildBulge;

    /// <summary>诊断用：子环形变动画是否还在跑。</summary>
    internal bool IsChildBulgeAnimatingForProbe => _view.IsChildBulgeAnimating;

    /// <summary>诊断用：立即把子环悬停形变落定（不播放动画）。</summary>
    internal void SetChildHoverImmediateForProbe(int index) => _view.SetChildHoverImmediate(index);

    /// <summary>诊断用：设置子项悬停（播放动画）。</summary>
    internal void SetChildHoverForProbe(int index) => _view.SetChildHover(index);

    /// <summary>诊断用：子弧「＋」的索引。</summary>
    internal int ChildAddSectorIndex => _view.ChildAddSectorIndex;

    /// <summary>诊断用：编辑面板是否开着且真的在视觉树上。</summary>
    internal bool IsPanelOpen => _view.PanelAttached;

    /// <summary>诊断用：模拟一次右键落点，走与鼠标同一条分派。</summary>
    internal HitResult RightClickForProbe(Point localPoint)
    {
        var hit = _view.HitTest(localPoint);
        HandleRightClick(hit);
        return hit;
    }

    /// <summary>诊断用：直接要求编辑某一项（跳过命中测试）。</summary>
    internal void ShowSectorEditForProbe(int rootIndex, int childIndex) =>
        _view.ShowSectorEdit(rootIndex, childIndex);

    /// <summary>诊断用：确认面板真的被加进了视觉树。</summary>
    internal bool PanelAttached => _view.PanelAttached;

    /// <summary>诊断用：当前是哪一块面板。</summary>
    internal string PanelKindForProbe => _view.PanelKindForProbe.ToString();

    /// <summary>诊断用：面板各行的文案。</summary>
    internal IReadOnlyList<string> PanelRowLabelsForProbe => _view.PanelRowLabelsForProbe;

    /// <summary>诊断用：点面板的第 index 行（走按钮同一个委托）。</summary>
    internal void InvokePanelRowForProbe(int index) => _view.InvokePanelRowForProbe(index);

    /// <summary>诊断用：改输入框文字并确认。</summary>
    internal void ConfirmPanelTextForProbe(string text) => _view.ConfirmPanelTextForProbe(text);

    /// <summary>诊断用：编辑态、当前菜单项数、以及「＋」扇区索引（三个一起看才判得清）。</summary>
    internal (bool EditMode, int ItemCount, int AddSector) RadialStateForProbe =>
        (_editMode, _menu.Items.Count, _view.AddSectorIndex);

    /// <summary>诊断用：面板在窗口内的矩形。</summary>
    internal Rect? PanelBoundsForProbe => _view.PanelBoundsForProbe;

    /// <summary>诊断用：面板当前编辑的是哪一项。</summary>
    internal (int RootIndex, int ChildIndex) EditTargetForProbe => _view.EditTargetForProbe;

    /// <summary>诊断用：走一遍 Esc 的处理路径。</summary>
    internal void EscapeForProbe() => HandleEscape();

    /// <summary>诊断用：把某个扇区置为悬停（会启动形变动画，用于核对换树时回调是否残留）。</summary>
    internal void HoverForProbe(int index) => _view.SetHover(index);

    /// <summary>诊断用：按真实命中结果走一遍左键分派（含"面板开着时先收面板"）。</summary>
    internal HitResult LeftClickForProbe(Point localPoint)
    {
        _lastPointer = localPoint;
        var hit = _view.HitTest(localPoint);
        HandleLeftDown(hit, localPoint);
        HandleLeftUp(hit);
        return hit;
    }

    /// <summary>诊断用：只按下（不抬起），用来核对"按下不该执行"。</summary>
    /// <summary>诊断用：当前环上的扇区数。</summary>
    internal int SectorCountForProbe => _view.SectorCount;

    /// <summary>诊断用：每帧重建 N 个扇区几何的压力（量并发开销）。</summary>
    internal void StartGeometryStressForProbe(int sectorCount, int durationMs) =>
        _view.StartGeometryStressForProbe(sectorCount, durationMs);

    internal void StopGeometryStressForProbe() => _view.StopGeometryStressForProbe();

    internal bool IsGeometryStressRunningForProbe => _view.IsGeometryStressRunning;

    internal HitResult PressForProbe(Point localPoint)
    {
        // 同时把 _lastPointer 摆到按下点：真实使用里指针必然在那里，
        // 而探针不会真的移动鼠标，不摆的话长按的位置判定拿到的是上一次的位置。
        _lastPointer = localPoint;

        var hit = _view.HitTest(localPoint);
        HandleLeftDown(hit, localPoint);
        return hit;
    }

    /// <summary>诊断用：模拟指针移动（走与真实 MouseMove 同一条预览刷新路径）。</summary>
    internal void MovePointerForProbe(Point localPoint)
    {
        _lastPointer = localPoint;
        UpdateDragPreview();
    }

    /// <summary>诊断用：是否正在拖拽。</summary>
    internal bool IsDraggingForProbe => _view.IsDragging;

    /// <summary>诊断用：被拖的是子环里的项还是第一层的。</summary>
    internal bool DragIsChildForProbe => _view.DragIsChild;

    /// <summary>诊断用：子环的编辑态虚线是否画出来了。</summary>
    internal bool ChildEditOutlineAttached => _view.ChildEditOutlineAttached;

    /// <summary>诊断用：把粒子场挂上/摘下（接口由探针定义，这里只做转发）。</summary>
    internal void AttachParticleFieldForProbe(object? field) =>
        ParticleFieldHost.Attach(_view.Root, _view.Center, field);

    /// <summary>诊断用：按下时判定的扇区索引（-1 = 不在真实项上，长按不会触发）。</summary>
    internal int PressedIndexForProbe => _pressedIndex;

    /// <summary>诊断用：按下时判定为子环里的项。</summary>
    internal bool PressedIsChildForProbe => _pressedIsChild;

    /// <summary>诊断用：当前落点格子。</summary>
    internal int DragTargetSlotForProbe => _view.DragTargetSlot;

    /// <summary>诊断用：目标格子标记是否真的画在视觉树上。</summary>
    internal bool DragMarkerAttachedForProbe => _view.DragMarkerAttached;

    /// <summary>诊断用：只抬起（配合上一次按下），用来核对"移到别处松开不执行"。
    internal HitResult ReleaseForProbe(Point localPoint)
    {
        var hit = _view.HitTest(localPoint);
        HandleLeftUp(hit);
        return hit;
    }

    /// <summary>诊断用：收起编辑面板。</summary>
    internal void DismissPanelForProbe() => _view.DismissPanel();

    /// <summary>诊断用：把窗口内坐标交给视图做命中测试。</summary>
    internal HitResult HitTestForProbe(Point localPoint) => _view.HitTest(localPoint);

    /// <summary>诊断用：按真实命中结果走一遍点击分派（事件照常发出）。</summary>
    internal HitResult ClickForProbe(Point localPoint)
    {
        var hit = _view.HitTest(localPoint);
        HandleClick(hit);
        return hit;
    }

    internal void CollapseChildArc() => _view.CollapseChildArc();

    /// <summary>开始在可见期间轮询指针，以推进悬停延迟。</summary>
    public void StartInteraction()
    {
        _hoverMachine.Reset();
        _lastPointer = null;
        _pointerPoll.Start();
    }

    /// <summary>停止轮询。窗口隐藏时必须调用。</summary>
    /// <summary>
    /// 休眠：把窗口挪到屏幕外，**不卸载它**。
    ///
    /// 为什么不用 <c>Hide()</c>：WPF 的 <c>Window.Hide()</c> 会销毁 HWND 与渲染目标，
    /// 下次 <c>Show()</c> 要重建 —— 实测那一帧要 **30~60ms**（纯映射成本，
    /// 与动画无关：整窗缩放、逐格入场、甚至不做动画都一样），而每次唤出都要付一次。
    /// 那正是"动画不够流畅"里最大的单个卡顿，而它落在动画最开始的几帧上。
    ///
    /// 挪到屏幕外只是一个 <c>SetWindowPos</c>：画面消失的效果一样，成本几乎为零。
    /// 注意窗口仍然是"可见"的，所以凡是按可见性判断的调用方（z 序层）要改用
    /// 覆盖层自己的逻辑状态，不能再看 <c>IsVisible</c>。
    /// </summary>
    public void Park()
    {
        if (!IsVisible) return;          // 还没映射过：交给首次 Show

        StopInteraction();
        ResetInteraction();

        var handle = (HWND)HandleValue;
        if (handle == default) return;

        PInvoke.SetWindowPos(handle, default, ParkedX, ParkedY, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    /// <summary>休眠时停放的坐标。取一个任何显示器布局都到不了的位置。</summary>
    private const int ParkedX = -32000;
    private const int ParkedY = -32000;

    /// <summary>
    /// 让休眠中的窗口里装着**空的环**（而不是静止的完整圆盘）。
    ///
    /// 为什么必须这样：分层窗口在屏幕上显示的是**上一次提交的那张位图**。
    /// 清空视觉树只是"标脏"——真正重绘要等下一个渲染帧。所以在它重绘之前把窗口
    /// 挪回屏上，露出的仍是上一轮留下的完整圆盘：用户看到的就是
    /// "先出现一个完整圆盘，然后它才消失、重新逐格长出来"（实测确实抓到 1 帧）。
    ///
    /// 而在休眠期间清空则完全没有时序问题：从休眠到下次唤出至少隔几十毫秒，
    /// 那次重绘早就提交了；等下次把窗口挪回屏上时，位图里本来就是空的。
    /// </summary>
    public void ClearForDormancy() => _view.PrepareEntrance();

    public void StopInteraction()
    {
        _pointerPoll.Stop();
        _hoverMachine.Reset();
        _lastPointer = null;
    }

    /// <summary>
    /// 复位交互状态（悬停高亮 + 逐帧形变动画）。
    /// 隐藏时必须调用 —— 形变动画挂的是 CompositionTarget.Rendering 回调，
    /// 不摘掉的话窗口隐藏后它还会继续逐帧跑，变成看不见的 CPU 占用。
    /// </summary>
    public void ResetInteraction()
    {
        _view.DismissPanel();
        _view.ResetInteraction();
    }

    /// <summary>把窗口重新插回 topmost band 最前（不改变位置与尺寸）。</summary>
    public void ReassertTopmost()
    {
        if (!IsVisible) return;
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        ZOrderManager.BringToTopmostFront(hwnd);
    }

    // ---------------------------------------------------------------- 动画

    /// <summary>
    /// 入场动画：167ms、scale 0.9→1.0、透明度 0→1，曲线是官方 Motion 规范里的
    /// <c>cubic-bezier(0,0,0,1)</c>（直接入场）。WPF 的 KeySpline 能精确表达同一条曲线。
    ///
    /// 系统关掉「在 Windows 中显示动画」（无障碍设置）时全部时长归零 —— 这类偏好必须尊重。
    /// </summary>
    /// <summary>
    /// 把入场动画的**起始状态**先落定。必须在 <c>Show()</c> **之前**调用。
    ///
    /// 少了这一步，窗口会先以终态渲染一两帧、动画才开始 —— 看起来就是"先闪一下再缩放"。
    /// 原因是 Show() 之后合成器可能立刻取走一帧，而 BeginAnimation 要到下一帧才生效；
    /// 于是那一帧画的是还没被动画接管的属性值（终态）。
    ///
    /// 程序启动后**首次**唤出时最明显：那一次窗口刚被映射，首帧的取走更早。
    /// </summary>
    public void PrepareEntrance()
    {
        if (!AnimationsEnabled())
        {
            ResetAnimationState();
            return;
        }

        // 先清掉可能残留的 HoldEnd（上一轮退场动画留下的），再把起始值写死。
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _view.Root.BeginAnimation(UIElement.OpacityProperty, null);

        if (_tokens.Motion.SequentialEntrance)
        {
            // 逐格入场：窗口整体立刻在场，只有环在逐格长出来。
            // 起始状态落定在**视图**上（每格零厚度、图标文字透明）；
            // 窗口层的不透明度必须留 1，否则会连"还没长出来的那几格"一起淡入。
            _scale.ScaleX = _scale.ScaleY = 1.0;
            _view.Root.Opacity = 1.0;
            _view.PrepareEntrance();
            return;
        }

        _scale.ScaleX = _scale.ScaleY = _tokens.Motion.EntranceFromScale;
        _view.Root.Opacity = 0;
    }

    /// <summary>诊断用：某格当前的逐格入场进度（0 = 还没长出来）。</summary>
    internal double EntranceRevealForProbe(int index) => _view.EntranceRevealOf(index);

    internal bool IsEntranceAnimatingForProbe => _view.IsEntranceAnimating;

    internal double MarkerOpacityForProbe(int index) => _view.MarkerOpacityForProbe(index);

    internal double EntranceStartDelayForProbe(int index) => _view.EntranceStartDelayOf(index);

    internal double CenterHintRevealForProbe => _view.CenterHintReveal;

    /// <summary>诊断用：入场起始状态是否已经落定（缩放值, 透明度）。</summary>
    internal (double Scale, double Opacity) EntranceStateForProbe => (_scale.ScaleX, _view.Root.Opacity);

    public void PlayEntrance()
    {
        var motion = _tokens.Motion;
        _view.SetHover(-1);

        if (!AnimationsEnabled())
        {
            ResetAnimationState();
            return;
        }

        if (motion.SequentialEntrance)
        {
            _view.PlaySequentialEntrance();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(motion.EntranceDurationMs);
        var spline = BuildKeySpline(motion.EntranceEasing);

        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            FromTo(motion.EntranceFromScale, 1.0, duration, spline));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            FromTo(motion.EntranceFromScale, 1.0, duration, spline));
        _view.Root.BeginAnimation(UIElement.OpacityProperty,
            FromTo(0.0, 1.0, duration, spline));
    }

    /// <summary>
    /// 退场动画。**刻意不返回可等待的任务、也不阻塞调用方** ——
    /// 动作执行必须立刻发生，动画只是视觉收尾。
    /// Kando 的 #650 就是因为在退场淡出之后才启动应用、让用户感到延迟而被抱怨。
    /// </summary>
    public void PlayExit()
    {
        var motion = _tokens.Motion;

        if (!AnimationsEnabled())
        {
            ResetAnimationState();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(motion.ExitDurationMs);
        var spline = BuildKeySpline(motion.ExitEasing);

        _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            FromTo(1.0, motion.ExitToScale, duration, spline));
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            FromTo(1.0, motion.ExitToScale, duration, spline));
        _view.Root.BeginAnimation(UIElement.OpacityProperty,
            FromTo(1.0, 0.0, duration, spline));
    }

    /// <summary>
    /// 把动画拉回静止状态。隐藏后必须调用 —— 否则动画的 HoldEnd 值会留在属性上，
    /// 下次显示会从"退场结束时的缩放"开始，看起来像闪了一下。
    /// </summary>
    public void ResetAnimationState()
    {
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _view.Root.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1.0;
        _view.Root.Opacity = 1.0;

        // 逐格入场的状态在**视图**上（扇区几何 + 图标文字透明度），也要一起复位：
        // 否则"隐藏 → 再唤出"之间会留着上一轮长到一半的圆盘。
        _view.CompleteEntranceImmediate();
    }

    /// <summary>
    /// 是否播放动画。用户显式设了就用他的选择，否则跟随系统
    /// 「在 Windows 中显示动画」的无障碍开关。
    /// </summary>
    private bool AnimationsEnabled() => MotionPreference.IsEnabled(_animationsOverride);

    private static KeySpline BuildKeySpline(string easing)
    {
        if (!MotionTokens.TryParseEasing(easing, out var p))
            p = (0, 0, 0, 1);   // 解析失败时退回 Fluent 的直接入场曲线

        return new KeySpline(new Point(p.X1, p.Y1), new Point(p.X2, p.Y2));
    }

    /// <summary>
    /// 用两帧关键帧表达从 from 到 to 的曲线。
    /// 之所以不用 DoubleAnimation + EasingFunction：WPF 内置的 CubicEase 是固定曲线，
    /// 要精确复现 <c>cubic-bezier(0,0,0,1)</c> 只能走 KeySpline。
    /// </summary>
    private static DoubleAnimationUsingKeyFrames FromTo(
        double from, double to, TimeSpan duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), spline));
        return animation;
    }

    // ---------------------------------------------------------------- 输入

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        _lastPointer = e.GetPosition(_view.Root);
        UpdateDragPreview();
        EvaluateHover();
    }

    /// <summary>拖拽中让被拖项跟着指针（只取角度，半径不影响落点）。</summary>
    private void UpdateDragPreview()
    {
        if (!_view.IsDragging || _lastPointer is not { } local) return;

        _view.UpdateDrag(_view.PolarOf(local).AngleDeg);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // 指针离开窗口，等价于落在环外 —— 让状态机去安排收起。
        _lastPointer = null;
        _hoverMachine.OnPointer(new HoverProbe(HitZone.Outside, -1, false, NowMs()));
        ApplyHoverAction(HoverAction.ScheduleClose);
    }

    private static long NowMs() => Environment.TickCount64;

    /// <summary>
    /// 把当前指针位置喂给状态机并执行它要求的动作。
    /// 由 MouseMove 与轮询计时器共同调用 —— 轮询那一路是"指针不动也要推进延迟"的实现。
    /// </summary>
    private void EvaluateHover()
    {
        // 拖拽中不推进悬停状态机：否则指针扫过别的扇区会一路展开子菜单，
        // 而用户正在做的事是"把这一项拖到那边去"。
        if (_view.IsDragging)
        {
            ApplyHighlight(-1, -1);
            return;
        }

        // **按住期间也冻结悬停。** 按下之后用户不是在用悬停导航，而是在点击或长按，
        // 这两种意图都不该让盘面在指下变动。
        //
        // 这一条是被用户报的问题逼出来的：子环里按住不动时，指针落在子项上，
        // 状态机认为"你离开父项了"，350ms 后把子弧收掉 —— 于是 1 秒长按触发时
        // 子环已经没了，拖拽要么拖不动、要么拖到了别的东西上。
        if (_pressed is not null)
        {
            ApplyHighlight(-1, -1);
            return;
        }

        // 编辑面板开着时不推进悬停状态机。
        // 面板通常压在某个扇区上，指针在那里会被判成"悬停在那个扇区"——
        // 于是面板开着的时候子弧会自己展开、收起，用户正在改的这一项还会高亮闪动。
        if (_view.IsPanelOpen)
        {
            ApplyHighlight(-1, -1);
            return;
        }

        if (_lastPointer is not { } local)
        {
            ApplyHighlight(-1, -1);
            return;
        }

        var hit = _view.HitTest(local);

        var probe = new HoverProbe(
            Zone: hit.Zone,
            Index: hit.Index,
            ItemHasChildren: hit.Zone == HitZone.Sector && _view.RootItemHasChildren(hit.Index),
            NowMs: NowMs());

        var action = _hoverMachine.OnPointer(probe);

        // 收起延迟到期这一条没有指针事件可依附，只能在这里按时间判断。
        if (action == HoverAction.None && _hoverMachine.CloseDelayElapsed(NowMs()))
            action = _hoverMachine.OnCloseTimerElapsed(NowMs());

        ApplyHoverAction(action);

        ApplyHighlight(
            hit.Zone == HitZone.Sector ? hit.Index : -1,
            hit.Zone == HitZone.ChildSector ? hit.Index : -1);
    }

    private void ApplyHoverAction(HoverAction action)
    {
        switch (action)
        {
            case HoverAction.OpenNow:
                if (_hoverMachine.OpenParent >= 0)
                    _view.ExpandChildArc(_hoverMachine.OpenParent);
                break;

            case HoverAction.CloseNow:
                _view.CollapseChildArc();
                break;

            // ScheduleOpen / ScheduleClose 不需要额外动作：
            // 轮询计时器会持续喂入指针位置，延迟到期时自然会转成 OpenNow / CloseNow。
            case HoverAction.ScheduleOpen:
            case HoverAction.ScheduleClose:
            case HoverAction.None:
            default:
                break;
        }
    }

    private void ApplyHighlight(int rootIndex, int childIndex)
    {
        // 死区与环外都不高亮：死区语义是「取消」，环外是「无效区域」。
        _view.SetHover(rootIndex);
        _view.SetChildHover(childIndex);
    }

    /// <summary>
    /// 按下的落点。为 null 表示当前没有正在进行的按下。
    /// 见 <see cref="HandleLeftUp"/> —— 只有"按下与抬起在同一处"才算一次完整点击。
    /// </summary>
    private HitResult? _pressed;

    /// <summary>按下的时刻；0 表示当前没有进行中的按下。</summary>
    private long _pressStartedAtMs;

    /// <summary>
    /// 长按过程中允许的指针移动量（DIP）。超过它就当作放弃长按。
    /// 取 14 大致是"手抖但不至于换项"的幅度。
    /// </summary>
    private const double LongPressMoveSlopDips = 14;

    /// <summary>按下时所在的扇区索引，-1 表示不在扇区上（死区/环外）。</summary>
    private int _pressedIndex = -1;

    private bool _pressedIsChild;

    /// <summary>按下时的窗口内坐标。长按只在指针没怎么移动时才成立。</summary>
    private Point _pressPoint;

    /// <summary>
    /// 这次按下是否已经变成拖拽（长按进入编辑态、或编辑态下按下）。
    /// 变成拖拽之后松手**不再执行**该项，而是走提交重排。
    /// </summary>
    private bool _pressBecameDrag;

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var local = e.GetPosition(_view.Root);
        HandleLeftDown(_view.HitTest(local), local);
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleLeftUp(_view.HitTest(e.GetPosition(_view.Root)));
    }

    /// <summary>
    /// 按下：**只记下落点，不做任何事**。
    ///
    /// 原来按下就直接执行了，问题很实在：按错了想挪开再松手也来不及 ——
    /// 程序已经启动了。改成"按住可以反悔"之后，误触只要移出扇区再松手即可。
    /// </summary>
    private void HandleLeftDown(HitResult hit, Point localPoint)
    {
        _pressPoint = localPoint;

        // 就地面板里的行自己处理按下事件并标记已处理，所以能走到这里说明按下的不是面板。
        // 面板开着时，第一下只用来收面板 —— 不让它顺手把下面的项执行掉。
        if (_view.IsPanelOpen)
        {
            _view.DismissPanel();
            _pressed = null;
            return;
        }

        _pressed = hit;

        // 长按计时从"按下"开始，并且只对真实项计时（死区、环外、「＋」都不算）：
        // 长按「＋」或空白处没有可编辑的对象，进了编辑态反而让人困惑。
        var onItem = !_view.IsAddSector(hit.Index) && !_view.IsChildAddSector(hit.Index);
        _pressedIndex = hit.Zone switch
        {
            HitZone.Sector when onItem => hit.Index,
            HitZone.ChildSector when onItem => hit.Index,
            _ => -1,
        };
        _pressedIsChild = hit.Zone == HitZone.ChildSector;
        _pressStartedAtMs = _pressedIndex >= 0 ? NowMs() : 0;
        _pressBecameDrag = false;

        // **编辑态下按下就直接进入拖拽**，不必再等一次长按。
        //
        // 长按的语义是"进入编辑态"；进了之后每次拖拽还要再按住一秒很烦，
        // 安卓也不是这样（进编辑态之后图标直接就能拖）。
        // 子环里的项同样：先悬停父项展开子环（200ms 悬停本来就会展开），
        // 再按子项即可拖 —— 所以编辑态下"点父项展开"这条路径不再需要。
        if (_editMode && _pressedIndex >= 0)
        {
            _pressBecameDrag = true;
            _view.BeginDrag(_pressedIndex, isChild: _pressedIsChild);
        }
    }

    /// <summary>
    /// 长按检查。搭在已有的 25ms 指针轮询上，不另起计时器 ——
    /// 这个轮询本来就在跑，多一个判断是零成本。
    ///
    /// 进入编辑态的同时**直接进入拖拽**（安卓那样一个连续手势）：
    /// 长按后还得松手再按一次会让人以为没反应。
    /// </summary>
    private void CheckLongPress()
    {
        // 编辑态下按下已经直接进入拖拽（见 HandleLeftDown），不需要长按计时。
        if (_editMode) return;

        if (_pressStartedAtMs == 0 || _pressBecameDrag || _pressedIndex < 0) return;
        if (_view.IsPanelOpen || _view.IsDragging) return;

        if (NowMs() - _pressStartedAtMs < _tokens.Interaction.LongPressMs) return;

        // 判定用**移动距离**而不是"是否还在同一扇区"。
        //
        // 用扇区判定太苛刻：按住两秒的过程中指针抖过一条缝（12 扇区时缝隙只有几度）
        // 长按就作废，而用户明明"按住没动"。这里给一个宽容度，与"按住可以反悔"
        // 依然一致 —— 真的想取消，手挪开就是了。
        if (_lastPointer is { } local)
        {
            var moved = Math.Sqrt(
                Math.Pow(local.X - _pressPoint.X, 2) + Math.Pow(local.Y - _pressPoint.Y, 2));
            if (moved > LongPressMoveSlopDips) return;
        }

        // **置位必须在所有提前返回之后。**
        // 放在前面的话：一旦在临界时刻因为"指针挪开了"而返回，标志已经置位，
        // 随后的松手会被当成"长按后的松手"而吞掉 —— 用户点一下完全没有反应，
        // 而且看不出任何原因。（这个顺序错误是被探针抓到的。）
        _pressBecameDrag = true;

        // 切编辑态会就地换掉整棵视觉树（多出「＋」扇区）。索引不会因此移位 ——
        // 「＋」永远排在所有项之后，所以按下时记下的索引在新树上仍然指向同一项。
        SetEditMode(true);

        _view.BeginDrag(_pressedIndex, isChild: _pressedIsChild);
    }

    /// <summary>
    /// 抬起：只有在**同一个区域、同一项**上按下又松开，才当成一次完整点击去执行。
    ///
    /// 中途移到别处（另一个扇区、环外、中心）都当作放弃 —— 这正是"按住可以反悔"的实现。
    /// 比较的是区域与索引，所以"按下在 A、松开在 B"不会执行 B：那会让用户在
    /// 想取消的时候反而触发了另一个程序。
    /// </summary>
    private void HandleLeftUp(HitResult released)
    {
        if (_pressed is not { } pressed) return;
        _pressed = null;

        _pressStartedAtMs = 0;

        // 这次按下已经变成拖拽：松手**不执行**该项，而是提交重排。
        // 少了这一条，长按会顺带把程序启动一次 —— 用户想编辑，结果打开了程序。
        if (_pressBecameDrag)
        {
            _pressBecameDrag = false;
            CommitDrag();
            return;
        }

        if (released.Zone != pressed.Zone || released.Index != pressed.Index) return;

        HandleClick(released);
    }

    /// <summary>松手：落在不同格子就请求重排；落回原处就什么都不做（不写盘）。</summary>
    private void CommitDrag()
    {
        var from = _pressedIndex;
        var wasDragging = _view.IsDragging;
        var to = _view.DragTargetSlot;

        _view.EndDrag();
        _pressedIndex = -1;

        if (!wasDragging || from < 0 || to < 0 || to == from) return;

        ReorderRequested?.Invoke(this, (_pressedIsChild ? _view.ChildArcParent : -1, from, to));
    }

    /// <summary>
    /// 右键 = 编辑鼠标下的那一项。
    ///
    /// 挑右键而不是长按或双击：右键在桌面程序里就是"对这一个东西做点什么"的既定语义，
    /// 不需要教学；而长按与双击在"指针移过去就已经有悬停行为"的轮盘上会互相干扰。
    /// </summary>
    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (_view.IsPanelOpen)
        {
            _view.DismissPanel();
            return;
        }

        HandleRightClick(_view.HitTest(e.GetPosition(_view.Root)));
    }

    /// <summary>右键落点的分派。与左键理由相同：抽出来让探针能走同一条路径。</summary>
    private void HandleRightClick(HitResult hit)
    {
        switch (hit.Zone)
        {
            case HitZone.Sector when !_view.IsAddSector(hit.Index):
                _view.ShowSectorEdit(hit.Index, -1);
                break;

            case HitZone.ChildSector when !_view.IsChildAddSector(hit.Index):
                if (_view.ChildArcParent >= 0) _view.ShowSectorEdit(_view.ChildArcParent, hit.Index);
                break;

            // 右键在「＋」上、死区上、环外：都当作"取消"。
            // 与左键一致总比"右键在某些地方没反应"好 —— 后者会让人以为程序卡住了。
            default:
                DismissRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// 点击落点的分派。
    ///
    /// 抽成独立方法是为了让探针能走**同一条**分派逻辑。探针合成不了真实的鼠标按键
    /// （那需要 SendInput 加前台焦点，而且会弹出文件对话框把探针卡住），
    /// 但它可以把命中结果喂进来。不抽的话，「点「＋」会请求加项」这件事就只能靠
    /// 读代码确认，而不是验证。
    /// </summary>
    private void HandleClick(HitResult hit)
    {
        switch (hit.Zone)
        {
            case HitZone.ChildSector:
                if (_view.ChildArcParent < 0) break;

                if (_view.IsChildAddSector(hit.Index))
                    // 子环的「＋」也先出面板 —— 两个「＋」行为不一致正是用户困惑的来源：
                    // 点了一个直接进文件浏览器，就完全看不到"新建分组"（它在另一个「＋」上）。
                    _view.ShowAddChoice(hit.Index, isChild: true);
                else if (!_editMode)
                    // 子项是叶子：执行它。父索引与子索引一起报上去，由上层解析成具体动作。
                    // **编辑态下不执行**：编辑时误触启动了程序，代价比"多点一次"大得多。
                    ItemActivated?.Invoke(this, (_view.ChildArcParent, hit.Index));
                break;

            case HitZone.Sector:
                if (_view.IsAddSector(hit.Index))
                {
                    // 第一层的「＋」先给一个选择：加程序，还是建一个带子菜单的分组。
                    // 原来这里直接进文件浏览器，于是"新建分组"这条路径根本不存在。
                    _view.ShowAddChoice(hit.Index);
                }
                else if (_view.RootItemHasChildren(hit.Index))
                {
                    // 点击有子项的扇区 = 跳过悬停延迟立即展开。
                    // **编辑态下也照常展开** —— 要重排子环里的项，就得先进得去。
                    _hoverMachine.OpenImmediately(hit.Index);
                    _view.ExpandChildArc(hit.Index);
                    _view.SetHover(hit.Index);
                }
                else if (!_editMode)
                {
                    ItemActivated?.Invoke(this, (hit.Index, -1));
                }
                break;

            case HitZone.DeadZone:
                // 中心 = 「返回上一层」。有子弧展开时先收子弧，否则取消整棵菜单。
                // 这与饼菜单的既定语义一致（Don Hopkins 的规范里中心就是取消，
                // Kurtenbach 的标记菜单里指向子菜单中心则是返回上一级）。
                //
                // 编辑态下中心是「完成」：先退出编辑态，**不关覆盖层** ——
                // 与 Esc 一样逐层退出，不做"一次点掉两层"。
                if (_editMode && !_view.IsChildArcVisible)
                {
                    SetEditMode(false);
                }
                else if (_view.IsChildArcVisible)
                {
                    _hoverMachine.Reset();
                    _view.CollapseChildArc();
                }
                else
                {
                    DismissRequested?.Invoke(this, EventArgs.Empty);
                }
                break;

            case HitZone.ChildArcGap:
                // 缝隙上的点击不做事 —— 它既不是选项也不是"外侧空白"。
                break;

            case HitZone.Outside:
                // 圆盘外一律关闭。本窗口是矩形而圆盘是圆的，四角的点击会被本窗口
                // 收走、遮罩层收不到 —— 所以必须无条件兜底，否则在四角点击毫无反应。
                //
                // 编辑态下第一次点空白只退出编辑态（与 Esc 分层退出一致），
                // 否则编辑到一半手一滑点到空白，整个覆盖层就没了、改动也没机会看。
                if (_editMode) SetEditMode(false);
                else DismissRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        HandleEscape();
    }

    /// <summary>
    /// Esc 的处理。抽成方法是为了让探针能走同一条路径 ——
    /// 密钥事件没法从探针里可靠地合成（要靠 SendInput，还得保证前台窗口是对的），
    /// 但"先收面板、再返回上一层、最后才关闭"这个优先级顺序恰恰是最容易写反的地方。
    /// </summary>
    private void HandleEscape()
    {
        // 从最内层开始逐层退出，是用户对 Esc 的一贯预期。
        if (_view.IsPanelOpen)
        {
            _view.DismissPanel();
            return;
        }

        // 编辑态在"返回上一层"之外层：先退出编辑，再谈返回与关闭。
        if (_editMode)
        {
            SetEditMode(false);
            return;
        }

        // 没有子弧时才关闭整棵菜单。
        if (_view.IsChildArcVisible)
        {
            _hoverMachine.Reset();
            _view.CollapseChildArc();
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
