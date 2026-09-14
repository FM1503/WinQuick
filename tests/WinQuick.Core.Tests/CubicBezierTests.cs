using WinQuick.Core.Motion;

namespace WinQuick.Core.Tests;

/// <summary>
/// 三次贝塞尔缓动求值。
///
/// 需要它的原因：WPF 的 KeySpline 能在声明式动画里精确使用这条曲线，但不提供求值接口。
/// 而"悬停形变"这类需要逐帧自己算缓动值的动效没法交给 WPF 去插值。
/// </summary>
public class CubicBezierTests
{
    [Fact]
    public void LinearCurve_IsIdentity()
    {
        var linear = CubicBezier.Linear;
        foreach (var t in new[] { 0.0, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0 })
            Assert.Equal(t, linear.Ease(t), 1e-4);
    }

    [Fact]
    public void EndpointsAreAlwaysExact()
    {
        var curve = CubicBezier.FluentDirectEntrance;
        Assert.Equal(0.0, curve.Ease(0.0), 1e-9);
        Assert.Equal(1.0, curve.Ease(1.0), 1e-9);
        // 越界输入应被夹住，而不是外推
        Assert.Equal(0.0, curve.Ease(-0.5), 1e-9);
        Assert.Equal(1.0, curve.Ease(1.5), 1e-9);
    }

    [Fact]
    public void FluentDirectEntrance_IsAStrongEaseOut()
    {
        // cubic-bezier(0,0,0,1) 的特征：起步就很快、后段趋缓。
        // 进度过半时应当已经走完了大半路程 —— 这是它"感觉不到延迟"的来源。
        var curve = CubicBezier.FluentDirectEntrance;

        var half = curve.Ease(0.5);
        Assert.True(half > 0.8, $"进度 0.5 时应已完成 80% 以上，实际 {half:0.###}");

        var quarter = curve.Ease(0.25);
        Assert.True(quarter > half * 0.4, "曲线应单调递增");
        Assert.True(curve.Ease(0.9) > curve.Ease(0.5));
    }

    [Fact]
    public void Ease_IsMonotonicallyIncreasing()
    {
        // 单调性是最基本的要求：缓动曲线若回退，动画会出现看得见的抽动。
        var curve = CubicBezier.FluentDirectEntrance;
        var previous = -1.0;

        for (var i = 0; i <= 100; i++)
        {
            var value = curve.Ease(i / 100.0);
            Assert.True(value >= previous - 1e-6, $"在 t={i / 100.0:0.##} 处出现了回退");
            previous = value;
        }
    }

    [Theory]
    // 参考值按 CSS 定义手算（先解 x(t)=p 得到 t，再取 y(t)）：
    //   cubic-bezier(0,0,0,1)       x(t)=t³ → t=0.5^(1/3)=0.7937，y=3(1-t)t²+t³=0.890
    //   cubic-bezier(0.55,0.55,0,1) x(t)=1.65t(1-t)²+t³，x=0.5 → t≈0.75，y≈0.921
    //   cubic-bezier(1,0,1,1)       x(t)=3t(1-t)+t³，x=0.5 → t≈0.207，y≈0.111
    [InlineData("cubic-bezier(0,0,0,1)", 0.5, 0.890)]
    [InlineData("cubic-bezier(0.55,0.55,0,1)", 0.5, 0.921)]
    [InlineData("cubic-bezier(1,0,1,1)", 0.5, 0.111)]
    public void KnownCurves_MatchReferenceValues(string text, double progress, double expected)
    {
        // 注意：这几个期望值是算出来的，不是凭印象写的。
        // 第一版我随手填了 0.94 / 0.66 / 0.31，全错 —— 测试立刻抓到了。
        var curve = CubicBezier.Parse(text);
        Assert.Equal(expected, curve.Ease(progress), 0.02);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ease-in-out")]
    [InlineData("cubic-bezier(0,0,0)")]
    [InlineData("cubic-bezier(a,b,c,d)")]
    public void MalformedInput_FallsBackToLinear(string? text)
    {
        // 解析失败必须退化而不是抛异常 —— 规格文件写错时界面应该还能用，
        // 只是动画变成线性。抛异常会让整个轮盘打不开，代价完全不成比例。
        var curve = CubicBezier.Parse(text);
        Assert.Equal(0.5, curve.Ease(0.5), 1e-4);
    }

    [Fact]
    public void Parse_AgreesWithTokenEasing()
    {
        // 规格里的曲线字符串必须能被这个求值器正确解析 ——
        // 否则声明的动画用的是 KeySpline、逐帧动画用的是另一条曲线，两者观感不一致。
        var tokens = Design.DesignTokens.Parse(
            File.ReadAllText(FindTokensFile()));
        Assert.True(Design.MotionTokens.TryParseEasing(tokens.Motion.EntranceEasing, out var points));
        Assert.Equal(0.0, points.X1);
        Assert.Equal(1.0, points.Y2);
    }

    private static string FindTokensFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "design", "tokens.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到 design/tokens.json");
    }
}
