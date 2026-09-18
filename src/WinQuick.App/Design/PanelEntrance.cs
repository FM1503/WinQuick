using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinQuick.App.Design;

/// <summary>
/// 覆盖层一侧的小面板（设置面板、皮肤面板）的入场动画。
///
/// 抽成一份是因为第二个面板出现了：两处各写一遍的话，那份"必须先清残留动画再写起始值"
/// 的讲究（见下）迟早只有一处保留，而它正是用户报过的"面板先闪一下再缩放"的根因。
///
/// 变换必须挂在**根内容元素**上，不能挂在 Window 上：WPF 的 Window 会对 RenderTransform
/// 做强制转换（<c>CoerceRenderTransform</c>）并抛「转换对于 Window 无效」。
/// 轮盘窗口当初就是为此把变换挂在根视觉元素上的，而设置面板的第一版忘了这一点 ——
/// 结果一打开面板就崩，表现正是用户报的"点设置没有显示设置界面"。
/// </summary>
internal static class PanelEntrance
{
    /// <summary>入场缩放的起点。比轮盘（0.86）收敛，面板是"贴着屏幕的一块"，不该弹。</summary>
    private const double FromScale = 0.94;

    private static readonly ConditionalWeakTable<Window, ScaleTransform> Scales = [];

    /// <summary>
    /// 把入场动画的**起始状态**先落定。必须在 <c>Show()</c> **之前**调用 ——
    /// 否则面板会先以终态渲染一两帧、动画才开始，看起来是"先闪一下再缩放"。
    /// </summary>
    public static void Prepare(Window window)
    {
        if (window.Content is not FrameworkElement root) return;
        if (!MotionPreference.SystemAllows) return;      // 不播动画就别预先缩小

        // **先把上一轮残留的动画清掉，再写起始值。**
        //
        // Play 挂的是关键帧动画，默认 HoldEnd —— 动画结束后"动画值"会一直压着本地值。
        // 于是第二次打开面板时，这里写的 Opacity = 0 会被那条残留动画的 1 盖掉：
        // 面板先以终态完整显示几帧，然后才被新动画拽回 0 重新长出来。
        // 判据在 --flash-probe 的"入场状态（应用侧逐帧）"：触发后首帧必须是 0。
        root.BeginAnimation(UIElement.OpacityProperty, null);

        if (root.RenderTransform is ScaleTransform previous)
        {
            previous.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            previous.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        var scale = new ScaleTransform(FromScale, FromScale);
        Scales.AddOrUpdate(window, scale);

        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;
        root.Opacity = 0;
    }

    /// <summary>播放入场（时长与曲线取当前皮肤的令牌）。</summary>
    public static void Play(Window window)
    {
        if (window.Content is not FrameworkElement root) return;

        var duration = TokenResources.Get<TimeSpan>("Motion.Duration");
        var spline = TokenResources.Get<KeySpline>("Motion.Easing");

        // 复用 Prepare 建好的那个变换：两处各建一个的话，
        // 显示的起始状态与动画的起点就会是两个不同的对象，改一处忘一处。
        var scale = Scales.TryGetValue(window, out var existing)
            ? existing
            : new ScaleTransform(FromScale, FromScale);

        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;

        var scaleAnimation = new DoubleAnimationUsingKeyFrames();
        scaleAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(FromScale, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(duration), spline));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration), spline));
        root.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
