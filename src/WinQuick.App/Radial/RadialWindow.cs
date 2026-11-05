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
using WinQuick.Core.Motion;
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
    private DesignTokens _tokens;
    private readonly ScaleTransform _scale = new(1, 1);

    /// <summary>
    /// 入场的旋转（"转进到位"）。与缩放一起挂在根内容的 <see cref="TransformGroup"/> 上 ——
    /// 分成两个 Transform 是为了让它们的时长与曲线各自独立（缩放带轻回弹、旋转收得稍缓）。
    /// </summary>
    private readonly RotateTransform _rotate = new(0);

    private readonly TransformGroup _transform = new();

    /// <summary>当前视觉树。换菜单/切编辑态时**就地替换它**，窗口本身不动。</summary>
    private RadialRingView _view;

    /// <summary>用户改过的高亮色。换视觉树时要重新应用 —— 它存在视图上，不在窗口上。</summary>
    private string? _accentHex;

    private bool _editMode;

    /// <summary>
    /// 当前盘面形态。与皮肤正交：皮肤换材质与动效，形态换单元外形。
    /// 它只在**重建视觉树**时用到（几何生成器是按形态选的），所以换形态＝一次重建。
    /// </summary>
    private RingForm _form = RingForm.Pie;

    /// <summary>当前这份菜单。就地换树时要用它作为"菜单没变、只切编辑态"的那一份。</summary>
    private MenuSpec _menu;

    /// <summary>
    /// 当前下钻到哪一级。环 1 画它的父层、环 2 画当前层 ——
    /// 「同时只看到两级」这件事全部由它决定（见 <see cref="LevelPath"/>）。
    /// </summary>
    private LevelPath _path = LevelPath.AtRoot;

    private readonly DispatcherTimer _pointerPoll;
    private Point? _lastPointer;

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
        _path = LevelPath.AtRoot;
        _view = RadialRingView.Build(menu, editMode: _editMode);
        Content = _view.Root;

        // 入场缩放挂在以圆心为原点的 ScaleTransform 上。
        // RenderTransformOrigin 必须是 0.5,0.5，否则圆盘会从左上角放大。
        // 变换必须挂在**根内容元素**上（Window 会强制转换 RenderTransform 并抛异常），
        // 且用 TransformGroup 把缩放与旋转都带上 —— 两者入场时同时进行、时长不同。
        _transform.Children.Add(_scale);
        _transform.Children.Add(_rotate);
        _view.Root.RenderTransformOrigin = new Point(0.5, 0.5);
        _view.Root.RenderTransform = _transform;

        // 指针轮询只做两件事：长按计时、悬停高亮同步。
        //
        // **子菜单的悬停展开已经退休**（用户要求"全部改成点击后再展开"）：
        // 原来的 200/350ms 延迟状态机与斜向缓冲整套都删掉了 ——
        // 悬停不再改变任何层级，只决定"哪一项被高亮"。
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

        SourceInitialized += (_, _) => EnsureToolWindowStyle();

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
    /// 某个叶子项被点击选中。参数是这一项在菜单树里的**完整路径**
    /// （第一层下标在前，最后一个是它自己在那一层里的下标）。
    ///
    /// 为什么不是当初那对（父索引, 子索引）：轮盘现在能一直往下钻，
    /// 两级的下标对不足以定位一项 —— 旧实现里"自己还有子项"的第二层项点下去
    /// 会被解析成 `Items[父].Children[子]`，它的 Action 是 null，于是报「还没有配置动作」。
    /// 交给整条路径之后可以解析到任意深度。
    /// </summary>
    public event EventHandler<IReadOnlyList<int>>? ItemActivated;

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

    /// <summary>
    /// 给窗口补上 WS_EX_TOOLWINDOW（幂等，可反复调用）。
    ///
    /// 为什么不能只在 SourceInitialized 里设一次：句柄存在之后 WPF 再改
    /// ShowActivated / Topmost 这类属性，会按内部记下的那份样式整体重算并覆写
    /// GWL_EXSTYLE —— 手工加的 TOOLWINDOW 位就被冲掉了。预热序列（Prewarm）
    /// 里就有一笔 `ShowActivated = true` 的回改，正好落在句柄创建之后。
    /// 实测后果：停放中的轮盘漏进任务视图（Win+Tab），每个虚拟桌面都会列出它。
    /// 所以入口处要先读、已含该位就直接返回（不发 SetWindowLong），
    /// 这样各处随时调用都无成本，自愈补样式也不会自己把自己无限触发。
    /// </summary>
    internal void EnsureToolWindowStyle()
    {
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        if (hwnd.IsNull) return;   // 还没建句柄：SourceInitialized 会再来一次

        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW)) return;

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
        EnsureToolWindowStyle();

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
        EnsureToolWindowStyle();

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
        // 换菜单一律回第一层：内容变过之后旧路径的下标可能已经移位。
        _path = LevelPath.AtRoot;
        ApplyMenuCore(menu, editMode, animateChildArc: false, levelShiftFrom: null, reopenChildArc: true);
    }

    /// <summary>
    /// 换一层（点击导航：展开 / 下钻 / 换分支 / 返回）。
    ///
    /// 走的是与换菜单同一条重建路径（复用画布、窗口不动），差别只有两点：
    /// 子弧按新路径展开时**带动画**；下钻时把旧环 2 的槽位交给
    /// <see cref="RadialRingView.PlayLevelShiftIn"/> 播"整层搬到环 1"。
    /// </summary>
    private void NavigateTo(LevelPath next, bool animate = true)
    {
        var from = _path;

        // 重建之前先把旧环 2 的槽位取走 —— 下钻时它是环 1 那一层的起点。
        var oldChildSpans = animate ? _view.ChildSpans : [];

        var shift = animate && next.Depth == from.Depth + 1 && oldChildSpans.Length > 0;

        _path = next;
        ApplyMenuCore(_menu, editMode: null, animateChildArc: animate,
            levelShiftFrom: shift ? oldChildSpans : null, reopenChildArc: false);
    }

    /// <param name="reopenChildArc">
    /// 换树之后要不要按"重建前展开的是谁"重新展开子弧。
    ///
    /// 换菜单/切编辑态要走这条（项数没变时把用户正在看的子环接着显示）；
    /// **导航绝不能走** —— 路径已经说清了该显示哪一层，再按旧索引展开会把
    /// 「返回第一层」当场弹回第一层展开态（这条是 --level-probe 抓出来的）。
    /// </param>
    private void ApplyMenuCore(
        MenuSpec menu, bool? editMode, bool animateChildArc, IReadOnlyList<SectorSpan>? levelShiftFrom,
        bool reopenChildArc)
    {
        var previousItemCount = _view.MenuItemCount;
        var previousOpenParent = _view.ChildArcParent;

        if (editMode is { } mode) _editMode = mode;
        _menu = menu;

        // **丢树之前必须先把旧树上的逐帧回调摘掉。**
        // CompositionTarget.Rendering 是静态事件，残留的订阅会让整个旧视觉树
        // 既不被回收、又每帧都在跑 —— 而换菜单恰恰发生在用户正在操作轮盘的时候，
        // 形变/子弧动画很可能正在播。这条由 --add-sector-probe 的回调计数核对锁住
        // （把这一行删掉，那条核对就会红）。
        _view.ResetInteraction();

        // 复用当前画布：换掉 Window.Content 会让整棵内容失效、走一次窗口级布局，
        // 在分层窗口上表现为闪一下（改一次菜单就闪一次）。
        var fresh = RadialRingView.Build(menu, editMode: _editMode, reuse: Content as Canvas, form: _form, path: _path);

        // 先把动画状态归零再挂新树：退场动画的 HoldEnd 值会留在 _scale 上，
        // 不清掉的话新内容会从"上次退场结束时的缩放"开始。
        ResetAnimationState();

        fresh.Root.RenderTransformOrigin = new Point(0.5, 0.5);
        fresh.Root.RenderTransform = _transform;

        UnwireViewEvents(_view);
        _view = fresh;
        WireViewEvents(_view);

        // 换内容对象会让整棵内容失效、走一次窗口级布局 —— 分层窗口上就是闪一下。
        // 复用画布时 Root 与当前 Content 是同一个实例（等值 SetValue 是空操作），
        // 但这一点要**数出来**而不是"应该没问题"：计数不为 0 就说明复用没生效。
        SurfaceOps.MenuRebuild(swapped: !ReferenceEquals(Content, _view.Root));
        Content = _view.Root;
        _view.SetAccentOverride(_accentHex);

        _lastPointer = null;

        // 子弧按路径展开：路径深于 0 就画当前层。
        if (_path.Depth >= 1)
        {
            ExpandChildArcForPath(immediate: !animateChildArc);
        }
        else if (reopenChildArc
            && previousOpenParent >= 0
            && previousItemCount == _view.MenuItemCount
            && _view.RootItemHasChildren(previousOpenParent))
        {
            // 增删子项这类**不改变项数**的变动后重新展开：用户正在看的就是它。
            ReopenChildArc(previousOpenParent);
        }

        // 位移最后播：它要写几何，而几何要等子树建好。
        if (levelShiftFrom is { Count: > 0 }) _view.PlayLevelShiftIn(levelShiftFrom);
    }

    /// <summary>按当前路径展开子弧。子项列表由路径解析出来 —— 视图不再自己去翻一级的 Children。</summary>
    private void ExpandChildArcForPath(bool immediate)
    {
        if (_path.Depth < 1) return;

        var parent = _path.Ring2Parent(_menu);
        if (parent is null || !parent.HasChildren) return;

        _view.ExpandChildArc(_path.ParentIndexInRing1, parent.Children, immediate);
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
    /// 外观变了（换皮肤 / 换形态）之后重建视觉树。
    ///
    /// 与 <see cref="ApplyMenu"/> 走**同一条**路径（复用画布、只失效子元素）：
    /// 那是实测"零像素变化"的内容刷新路径，而换皮肤恰恰是高频率操作 ——
    /// 用户在皮肤面板上一个个点过去，每次都必须立刻在轮盘上看到，且不能闪。
    ///
    /// 令牌也要重新取：<see cref="TokenStore.Current"/> 在换皮肤时换了对象，
    /// 而窗口构造时抓到的那份引用是旧的（渲染尺寸、动效都从它读）。
    /// </summary>
    public void RebuildForSkin(RingForm form)
    {
        _tokens = TokenStore.Current;
        _form = form;

        // 尺寸由令牌决定，皮肤**不许**改它（SkinCatalog 有守卫），但这里仍然重新读一次 ——
        // 免得"窗口尺寸"与"画布尺寸"哪天各自读了一份而悄悄不一致。
        Width = Height = _tokens.Radius.WindowSize;

        // **优先就地换材质，不重建视觉树。**
        // 实测：重建一次约净留 0.65MB（重建后的内容要重新渲染进分层窗口，
        // 那部分内存强制回收也只回来一半），而用户点几下皮肤就能累积几十 MB。
        // 就地换材质一个元素都不新建，就没有这笔账。
        if (_view.TryApplySkinInPlace(form))
        {
            // 光晕/几何那类结构差异走不通就地更新时，它自己会返回 false，落到下面的重建。
            SurfaceOps.MenuRebuild(swapped: false);
            EnsureToolWindowStyle();
            return;
        }

        ApplyMenu(_menu);
        EnsureToolWindowStyle();
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

    /// <summary>
    /// 实时更新悬停延迟。
    ///
    /// **现在什么也不做**：悬停展开已退休（用户要求全部改成点击展开），
    /// 那两个延迟不再影响任何行为。设置面板与 config.json 里的两项暂时留着，
    /// 等用户决定是否连同 UI 一起撤掉。
    /// </summary>
    public void ApplyHoverDelays(int? openMs, int? closeMs)
    {
        _ = openMs;
        _ = closeMs;
    }

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
    /// 立刻重新展开某个父项的子弧（无动画）。
    ///
    /// 用在"往子环里加了一项、菜单重建之后"：不这么做的话用户加完一项，
    /// 子弧是收起的，他会以为"加进去的东西不见了"，得重新点一次才能确认。
    /// </summary>
    public void ReopenChildArc(int parentIndex)
    {
        if (!_view.RootItemHasChildren(parentIndex)) return;

        _path = LevelPath.AtRoot.SwitchBranch(parentIndex);
        ExpandChildArcForPath(immediate: true);
        _view.SetHover(parentIndex);
    }

    /// <summary>诊断用：展开某一项的子弧（等价于"点了一下它"）。探针走这条路径。</summary>
    internal void ExpandChildArc(int parentIndex)
    {
        if (!_view.RootItemHasChildren(parentIndex)) return;
        NavigateTo(_path.SwitchBranch(parentIndex), animate: false);
        _view.SetHover(parentIndex);
    }

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

    /// <summary>诊断用：收起子弧 = 回第一层（同时只显示两级，收起就是退到第一层）。</summary>
    internal void CollapseChildArc() => NavigateTo(LevelPath.AtRoot, animate: false);

    /// <summary>诊断用：当前的层级路径（下钻链）。</summary>
    internal IReadOnlyList<int> LevelStepsForProbe => _path.Steps;

    /// <summary>诊断用：当前显示到第几层（0 = 第一层）。</summary>
    internal int LevelDepthForProbe => _path.Depth;

    /// <summary>诊断用：环 2 的父项在环 1 里的下标。</summary>
    internal int ChildArcParentForProbe => _path.ParentIndexInRing1;

    /// <summary>诊断用：环 1 当前各项的标签（按顺序）。</summary>
    internal IReadOnlyList<string> Ring1LabelsForProbe => _view.Ring1Labels;

    /// <summary>诊断用：环 1 里第 index 项是否有子项。</summary>
    internal bool Ring1ItemHasChildrenForProbe(int index) => _view.RootItemHasChildren(index);

    /// <summary>诊断用：环 2 当前各项的标签（按顺序）。</summary>
    internal IReadOnlyList<string> Ring2LabelsForProbe => _view.ChildLabelsOf(_path.ParentIndexInRing1);

    /// <summary>诊断用：环 2 里第 index 项自己还有子项吗。</summary>
    internal bool ChildIndexHasChildrenForProbe(int index) => _view.ChildIndexHasChildren(index);

    /// <summary>诊断用：层级位移（扇面展开）是否正在播。</summary>
    internal bool IsLevelShiftingForProbe => _view.IsLevelShifting;

    /// <summary>诊断用：层级位移的逐帧回调数（非 0 = 动画未结束或回调泄漏）。</summary>
    internal int LevelShiftHookCountForProbe => LevelShiftAnimator.ActiveHookCount;

    /// <summary>诊断用：右半「上一层」标签的透明度（灰态 ≈ 0.22，亮态 ≈ 0.55）。</summary>
    internal double CenterBackAlphaForProbe => _view.CenterBackAlphaForProbe;

    /// <summary>开始在可见期间轮询指针（长按计时 + 悬停高亮）。</summary>
    public void StartInteraction()
    {
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
    /// 让窗口进入"休眠态"：缩放与旋转归位，但**不透明度保持 0**。
    ///
    /// 为什么用不透明度而不是"把环内容清空"：分层窗口在屏幕上显示的是**上一次提交的位图**，
    /// 所以只要窗口自身是透明的，里面装的是什么都不会露出来 —— 残留终态从根上不可能出现。
    /// 而"清空内容"那条路必须配一个"下次唤出前恢复"的动作，两者一旦有一处漏掉，
    /// 就会表现为"空轮盘"或"只有图标的空环"（这两类都被用户实测撞到过）。
    /// **少一个状态，就少一类 bug。**
    /// </summary>
    public void ResetForDormancy()
    {
        ResetAnimationState();
        _view.Root.Opacity = 0;
    }

    public void StopInteraction()
    {
        _pointerPoll.Stop();
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
        // **三个属性一个都不能漏**：漏掉哪个，它就会在动画开始前先以"终态"闪一帧。
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _view.Root.BeginAnimation(UIElement.OpacityProperty, null);

        // 窗口整体从"略小 + 转了个角度 + 全透明"起步。
        _scale.ScaleX = _scale.ScaleY = _tokens.Motion.EntranceFromScale;
        _rotate.Angle = _tokens.Motion.EntranceFromRotateDeg;
        _view.Root.Opacity = 0;
    }

    /// <summary>诊断用：某格当前的填充/描边读数（换皮肤的功能核对着它）。</summary>
    internal (string Fill, string Stroke, double StrokeWidth) SectorStyleForProbe(int index) =>
        _view.SectorStyleForProbe(index);

    /// <summary>诊断用：某格当前的逐格入场进度（0 = 还没长出来）。</summary>
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

        // 时长与曲线来自 Core 的入场计划（`EntrancePlan`）—— 那边有单测锁住
        // "每条通道的终点都是终态"与"关掉动画时计划为空"，这里只负责把通道映射到 WPF 属性。
        var channels = EntrancePlan.Channels(motion, animationsEnabled: true);

        if (channels.Any(c => c.Name == EntrancePlan.ScaleChannel))
        {
            var channel = channels.Single(c => c.Name == EntrancePlan.ScaleChannel);
            var duration = TimeSpan.FromMilliseconds(channel.DurationMs);

            _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                MotionSplines.FromTo(channel.From, channel.To, duration, channel.Easing));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                MotionSplines.FromTo(channel.From, channel.To, duration, channel.Easing));
        }

        if (channels.Any(c => c.Name == EntrancePlan.RotateChannel))
        {
            var channel = channels.Single(c => c.Name == EntrancePlan.RotateChannel);
            _rotate.BeginAnimation(RotateTransform.AngleProperty,
                MotionSplines.FromTo(channel.From, channel.To,
                    TimeSpan.FromMilliseconds(channel.DurationMs), channel.Easing));
        }

        if (channels.Any(c => c.Name == EntrancePlan.FadeChannel))
        {
            var channel = channels.Single(c => c.Name == EntrancePlan.FadeChannel);
            _view.Root.BeginAnimation(UIElement.OpacityProperty,
                MotionSplines.FromTo(channel.From, channel.To,
                    TimeSpan.FromMilliseconds(channel.DurationMs), channel.Easing));
        }
        _view.Root.BeginAnimation(UIElement.OpacityProperty,
            FromTo(0.0, 1.0, TimeSpan.FromMilliseconds(motion.EntranceDurationMs),
                BuildKeySpline(motion.EntranceEasing)));
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
        _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _view.Root.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1.0;
        _rotate.Angle = 0;                 // 旋转也必须归零：残留角度会让整圈永久歪着
        _view.Root.Opacity = 1.0;
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
        // 指针离开窗口，等价于落在环外：清掉高亮即可。
        // （以前这里要"安排一次子菜单收起"，悬停展开退休后没有这件事了。）
        _lastPointer = null;
        ApplyHighlight(-1, -1);
    }

    private static long NowMs() => Environment.TickCount64;

    /// <summary>
    /// 悬停高亮同步。指针不动时也由轮询反复调用，所以只做"改属性"这一件事。
    ///
    /// **悬停不再展开子菜单**（用户要求：悬停即展开会降低选扇区的效率，全部改成点击展开），
    /// 所以这里没有任何状态机、没有延迟、也不改层级。
    /// </summary>
    private void EvaluateHover()
    {
        // 拖拽中、按住期间、面板开着时都不改高亮：
        // 这三种情况下指针底下的东西不是"用户想选的项"（正在拖它、正在按它、面板压着它）。
        if (_view.IsDragging || _pressed is not null || _view.IsPanelOpen || _lastPointer is not { } local)
        {
            ApplyHighlight(-1, -1);
            return;
        }

        var hit = _view.HitTest(local);
        ApplyHighlight(
            hit.Zone == HitZone.Sector ? hit.Index : -1,
            hit.Zone == HitZone.ChildSector ? hit.Index : -1);
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
                else if (_editMode)
                    // 编辑态下子环里的点击不做事：编辑支持的是**两级以内**
                    //（第一层 + 它的子项），再往下钻会让"加/改名/重排"落到哪一层变得含糊。
                    // 这与旧实现一致（旧实现里子项的点击在编辑态下也是被忽略的）。
                    break;
                else if (_view.ChildIndexHasChildren(hit.Index))
                    // **点击环 2 的分组扇区 = 进下一层**：旧的环 2 整层搬到环 1 的位置。
                    // 这就是"同时只看到两级"往前走的方式。
                    NavigateTo(_path.Descend(hit.Index));
                else
                    // 子项是叶子：执行它。报上去的是**完整路径**，上层据此解析出具体动作。
                    ItemActivated?.Invoke(this, _path.StepsToRing2(hit.Index));
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
                    // 点击有子项的扇区 = 展开它（**深度不变、只换分支**）：
                    // 环 1 还是这一层，环 2 换成被点那一项的子项。
                    // 编辑态下也照常展开 —— 要重排子环里的项，就得先进得去。
                    NavigateTo(_path.SwitchBranch(hit.Index));
                    _view.SetHover(hit.Index);
                }
                else if (!_editMode)
                {
                    ItemActivated?.Invoke(this, _path.StepsToRing1(hit.Index));
                }
                break;

            case HitZone.DeadZone:
                // 中心分成两半：**左半 = 返回第一层，右半 = 返回上一层**。
                // 分区不破坏命中模型：死区本来就是"不选中任何项"，
                // 两半各自的动作只是在这块空白上补两种语义。
                //
                // 关闭不在中心了 —— 它仍是"点环外空白"与 Esc（Esc 是分层退出：
                // 有层级时先退层，第一层再按一次才关闭）。
                HandleDeadZoneClick(hit.AngleDeg);
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
    /// 死区（中心）的分派：**左半 = Esc（关闭整棵轮盘），右半 = 返回上一层**。
    ///
    /// 左半在**任何深度**都直接关闭（等同点环外）—— 否则它在深层里的作用
    /// 和右半完全重复（这是与 Esc 键的唯一差别：Esc 键仍逐层退出）。
    /// 编辑态下左半是「退出编辑态」，不关覆盖层（与 Esc 一样分层退出）。
    /// 右半在第一层时不可用（界面上画灰、点了不动）。
    /// </summary>
    private void HandleDeadZoneClick(double angleDeg)
    {
        if (CenterZones.IsEscHalf(angleDeg))
        {
            if (_editMode)
            {
                SetEditMode(false);
                return;
            }

            DismissRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (CenterZones.IsBackAvailable(_path.Depth)) NavigateTo(_path.Back());
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

        // 有层级时先退一层；只在第一层时才关闭整棵菜单。
        // 这就是"Esc 逐层退出"：三层时按两次回第一层，第三次才关。
        if (_path.Depth > 0)
        {
            NavigateTo(_path.Back());
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
