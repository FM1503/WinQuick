using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
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

        SourceInitialized += (_, _) => ApplyExtendedStyles();

        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseDown;
        MouseRightButtonDown += OnMouseDown;
        KeyDown += OnKeyDown;
        Deactivated += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>要求关闭覆盖层（Esc / 点圆盘外 / 失去焦点）。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>某个叶子扇区被点击选中。</summary>
    public event EventHandler<int>? SectorActivated;

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
        PInvoke.SetWindowPos(hwnd, default, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
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

    /// <summary>系统「在 Windows 中显示动画」的无障碍开关。</summary>
    private static bool AnimationsEnabled()
    {
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
        var local = e.GetPosition(_view.Root);
        var hit = _view.HitTest(local);

        // 死区与环外都不高亮：死区语义是"取消"，环外是"无效区域"。
        _view.SetHover(hit.Zone == HitZone.Sector ? hit.Index : -1);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var local = e.GetPosition(_view.Root);
        var hit = _view.HitTest(local);

        e.Handled = true;

        switch (hit.Zone)
        {
            case HitZone.Sector:
                SectorActivated?.Invoke(this, hit.Index);
                break;

            case HitZone.DeadZone:
                // 中心 = 取消整棵菜单。这是饼菜单的既定语义，不需要额外提示。
                DismissRequested?.Invoke(this, EventArgs.Empty);
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
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
