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
    ///
    /// 这里**统一吃缓动文本**而不是 KeySpline：因为 <c>steps(n)</c>（离散阶梯）
    /// 根本表达不成一条贝塞尔曲线 —— 它要的是若干个离散关键帧。
    /// 让调用方各自判断"这条曲线能不能表示成 KeySpline"，迟早会有人漏掉一种写法。
    /// </summary>
    public static DoubleAnimationUsingKeyFrames FromTo(
        double from, double to, TimeSpan duration, string? easing)
    {
        if (MotionTokens.TryParseSteps(easing, out var steps))
            return FromToSteps(from, to, duration, steps);

        return FromToSpline(from, to, duration, FromToken(easing));
    }

    /// <summary>
    /// 已经拿着 <see cref="KeySpline"/> 的调用方用这条。
    /// **刻意不叫 FromTo**：缓动文本才是默认入口 —— 拿 spline 等于绕过了 steps(n)，
    /// 而"某个通道悄悄用了别的曲线"正是要避免的那类问题。
    /// </summary>
    public static DoubleAnimationUsingKeyFrames FromToSpline(
        double from, double to, TimeSpan duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration), spline));
        return animation;
    }

    /// <summary>
    /// 离散阶梯：n 段硬切（对应 CSS 的 <c>steps(n)</c>）。
    /// 复古皮肤的"一格一格跳"就是这条 —— 它没有过渡，只有跳变。
    /// </summary>
    public static DoubleAnimationUsingKeyFrames FromToSteps(
        double from, double to, TimeSpan duration, int steps)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };

        // 起点仍然要有一帧：WPF 在第一个关键帧之前用的是**第一个关键帧的值**，
        // 少了这一帧，"起始值"就永远不会出现在画面上（动画从第一格直接开始）。
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        for (var i = 1; i <= steps; i++)
        {
            var value = from + ((to - from) * i / steps);
            var at = TimeSpan.FromTicks(duration.Ticks * i / steps);
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(value, KeyTime.FromTimeSpan(at)));
        }

        return animation;
    }
}
