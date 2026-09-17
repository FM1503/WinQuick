using System.Windows;
using System.Windows.Media.Animation;
using WinQuick.Core.Design;

namespace WinQuick.App.Design;

/// <summary>
/// 把令牌里的 CSS 缓动文本转成 WPF 的 <see cref="KeySpline"/>，并给出项目统一的入场关键帧写法。
///
/// 抽成一份是因为这段代码已经在三处各写了一遍（TokenResources、RadialWindow、子环滑出）——
/// 三份实现迟早会因某处换了写法而不一致，而"曲线不一致"正好是肉眼看得出来、代码上却不显眼的那类问题。
/// </summary>
internal static class MotionSplines
{
    /// <summary>解析失败时退化为线性（与 <c>MotionTokens.TryParseEasing</c> 的约定一致）。</summary>
    public static KeySpline FromToken(string? easing)
    {
        if (!MotionTokens.TryParseEasing(easing, out var p))
            return new KeySpline(new Point(0, 0), new Point(1, 1));

        return new KeySpline(new Point(p.X1, p.Y1), new Point(p.X2, p.Y2));
    }

    /// <summary>
    /// 从 <paramref name="from"/> 到 <paramref name="to"/> 的关键帧动画。
    /// <c>HoldEnd</c> 是刻意的：入场动画结束后属性停在终值，直到显式清掉（见各自的 Reset）。
    /// </summary>
    public static DoubleAnimationUsingKeyFrames FromTo(
        double from, double to, TimeSpan duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), spline));
        return animation;
    }
}
