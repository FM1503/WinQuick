using System.Windows;
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
    private readonly RadialRingView _view;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly SubmenuHoverMachine _hoverMachine;
    private readonly DispatcherTimer _pointerPoll;
    private Point? _lastPointer;
    private bool _stylesApplied;

    public RadialWindow(MenuSpec menu)
    {
        _tokens = TokenStore.Current;

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

        _view = RadialRingView.Build(menu);
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
        _pointerPoll.Tick += (_, _) => EvaluateHover();

        SourceInitialized += (_, _) => ApplyExtendedStyles();

        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonDown += OnMouseDown;
        MouseRightButtonDown += OnMouseDown;
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

    /// <summary>实时更新高亮色。设置面板改色后立刻可见。</summary>
    public void ApplyAccent(string? hex) => _view.SetAccentOverride(hex);

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

    /// <summary>诊断用：子弧「＋」的索引。</summary>
    internal int ChildAddSectorIndex => _view.ChildAddSectorIndex;

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
    public void ResetInteraction() => _view.ResetInteraction();

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
    public void PlayEntrance()
    {
        var motion = _tokens.Motion;
        _view.SetHover(-1);

        if (!AnimationsEnabled())
        {
            ResetAnimationState();
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
    }

    /// <summary>
    /// 是否播放动画。用户显式设了就用他的选择，否则跟随系统
    /// 「在 Windows 中显示动画」的无障碍开关。
    /// </summary>
    private bool AnimationsEnabled()
    {
        if (_animationsOverride is { } explicitValue) return explicitValue;

        try
        {
            return SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            return true;
        }
    }

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
        EvaluateHover();
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

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HandleClick(_view.HitTest(e.GetPosition(_view.Root)));
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
                    AddItemRequested?.Invoke(this, _view.ChildArcParent);
                else
                    // 子项是叶子：执行它。父索引与子索引一起报上去，由上层解析成具体动作。
                    ItemActivated?.Invoke(this, (_view.ChildArcParent, hit.Index));
                break;

            case HitZone.Sector:
                if (_view.IsAddSector(hit.Index))
                {
                    AddItemRequested?.Invoke(this, -1);
                }
                else if (_view.RootItemHasChildren(hit.Index))
                {
                    // 点击有子项的扇区 = 跳过悬停延迟立即展开。
                    _hoverMachine.OpenImmediately(hit.Index);
                    _view.ExpandChildArc(hit.Index);
                    _view.SetHover(hit.Index);
                }
                else
                {
                    ItemActivated?.Invoke(this, (hit.Index, -1));
                }
                break;

            case HitZone.DeadZone:
                // 中心 = 「返回上一层」。有子弧展开时先收子弧，否则取消整棵菜单。
                // 这与饼菜单的既定语义一致（Don Hopkins 的规范里中心就是取消，
                // Kurtenbach 的标记菜单里指向子菜单中心则是返回上一级）。
                if (_view.IsChildArcVisible)
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
                DismissRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;

        // Esc 先「返回上一层」，没有子弧时才关闭整棵菜单。
        if (_view.IsChildArcVisible)
        {
            _hoverMachine.Reset();
            _view.CollapseChildArc();
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
