namespace WinQuick.Core.Motion;

/// <summary>
/// 三次贝塞尔缓动求值，等价于 CSS 的 <c>cubic-bezier(x1,y1,x2,y2)</c>。
///
/// 为什么需要自己实现：WPF 的 <see cref="System.Windows.Media.Animation.KeySpline"/> 能在
/// 动画里精确使用这条曲线，但**不提供求值接口**。而"悬停形变"这类由逐帧驱动矩阵/几何的动效
/// 需要在每一帧自己算出缓动值，没法交给 WPF 的动画系统去插值。
///
/// 曲线定义（P0=(0,0)、P3=(1,1) 固定）：
///   x(t) = 3(1-t)²t·x1 + 3(1-t)t²·x2 + t³
///   y(t) = 3(1-t)²t·y1 + 3(1-t)t²·y2 + t³
/// 给定进度 p 需要先解出 x(t)=p 的 t，再取 y(t)。x 在 [0,1] 上单调，用二分即可，
/// 不需要牛顿迭代 —— 二分 20 次在本项目的精度要求下已经远超需要。
/// </summary>
public readonly struct CubicBezier
{
    private readonly double _x1, _y1, _x2, _y2;

    public CubicBezier(double x1, double y1, double x2, double y2)
    {
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    /// <summary>Fluent 的直接入场曲线 <c>cubic-bezier(0,0,0,1)</c>：起步快、收尾缓。</summary>
    public static CubicBezier FluentDirectEntrance { get; } = new(0, 0, 0, 1);

    public static CubicBezier Linear { get; } = new(0, 0, 1, 1);

    /// <summary>把归一化进度 [0,1] 映射为缓动后的值。</summary>
    public double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;

        var t = SolveTForX(progress, out var solved);
        return solved ? Evaluate(_y1, _y2, t) : progress;   // 求解失败时退化为线性，不抛异常
    }

    private double SolveTForX(double x, out bool converged)
    {
        double low = 0, high = 1, t = x;

        for (var i = 0; i < 24; i++)
        {
            t = (low + high) / 2.0;
            var currentX = Evaluate(_x1, _x2, t);

            if (Math.Abs(currentX - x) < 1e-7) { converged = true; return t; }
            if (currentX < x) low = t;
            else high = t;
        }

        converged = true;   // 二分收敛到区间宽度 2^-24，足够
        return t;
    }

    /// <summary>三次贝塞尔在两个控制点下的分量值（端点固定为 0 与 1）。</summary>
    private static double Evaluate(double c1, double c2, double t)
    {
        var mt = 1 - t;
        return 3 * mt * mt * t * c1 + 3 * mt * t * t * c2 + t * t * t;
    }

    /// <summary>由 CSS 语法构造，例如 <c>cubic-bezier(0,0,0,1)</c>。解析失败返回线性。</summary>
    public static CubicBezier Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Linear;

        var open = text.IndexOf('(');
        var close = text.IndexOf(')');
        if (open < 0 || close < open) return Linear;

        var parts = text[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 4) return Linear;
        if (!double.TryParse(parts[0], out var x1) || !double.TryParse(parts[1], out var y1) ||
            !double.TryParse(parts[2], out var x2) || !double.TryParse(parts[3], out var y2))
            return Linear;

        return new CubicBezier(x1, y1, x2, y2);
    }
}
