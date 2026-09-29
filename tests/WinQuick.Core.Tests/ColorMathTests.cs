using WinQuick.Core.Design;

namespace WinQuick.Core.Tests;

/// <summary>
/// 调色盘的纯算术回归锁。用已知颜色对（红、Win11 蓝等）逐条锁死 ——
/// 这些换算错了的表现是「选出来的颜色不对」，肉眼比对很难发现。
/// </summary>
public class ColorMathTests
{
    [Theory]
    [InlineData(0, 1, 1, "#FF0000")]      // 纯红
    [InlineData(120, 1, 1, "#00FF00")]    // 纯绿
    [InlineData(240, 1, 1, "#0000FF")]    // 纯蓝
    [InlineData(0, 0, 1, "#FFFFFF")]      // 无饱和度 = 白
    [InlineData(0, 0, 0, "#000000")]      // 黑
    [InlineData(60, 1, 1, "#FFFF00")]     // 黄（六扇区的边界）
    [InlineData(300, 1, 1, "#FF00FF")]    // 品红
    public void HsvToRgb的已知值(double h, double s, double v, string hex)
    {
        var (r, g, b) = ColorMath.HsvToRgb(h, s, v);
        Assert.Equal(hex, ColorMath.ToHex(r, g, b));
    }

    [Fact]
    public void 色相环回_负角与超360都归一到同一结果()
    {
        var base360 = ColorMath.HsvToRgb(240, 1, 1);
        Assert.Equal(base360, ColorMath.HsvToRgb(240 - 360, 1, 1));
        Assert.Equal(base360, ColorMath.HsvToRgb(240 + 720, 1, 1));
    }

    [Fact]
    public void HsvRgb往返在色板全表上闭合()
    {
        foreach (var hex in ColorMath.RecommendedAccentPalette)
        {
            Assert.True(ColorMath.TryParseHex(hex, out var r, out var g, out var b), hex);

            var (h, s, v) = ColorMath.RgbToHsv(r, g, b);
            var back = ColorMath.HsvToRgb(h, s, v);

            // 往返允许 ±1（取整误差），不允许漂移
            Assert.True(Math.Abs(back.R - r) <= 1 && Math.Abs(back.G - g) <= 1 && Math.Abs(back.B - b) <= 1,
                $"{hex} 往返漂移：({r},{g},{b}) → ({back.R},{back.G},{back.B})");
        }
    }

    [Theory]
    [InlineData("#FF0000", 255, 0, 0)]
    [InlineData("FF8800", 255, 136, 0)]     // # 可省略
    [InlineData("#0078d4", 0, 120, 212)]
    public void TryParseHex接受完整六位(string text, int r, int g, int b)
    {
        Assert.True(ColorMath.TryParseHex(text, out var pr, out var pg, out var pb));
        Assert.Equal((r, g, b), (pr, pg, pb));
    }

    [Theory]
    [InlineData("#00")]        // 半截
    [InlineData("xyz")]
    [InlineData("#12345")]     // 五位
    [InlineData("#1234567")]   // 七位
    [InlineData("")]
    public void TryParseHex拒绝不完整与非法值(string text) =>
        Assert.False(ColorMath.TryParseHex(text, out _, out _, out _));

    [Fact]
    public void 最近使用_新值置前去重封顶()
    {
        var recent = new[] { "#A", "#B", "#C", "#D", "#E", "#F" };

        var updated = ColorMath.UpdateRecent(recent, "#NEW");
        Assert.Equal(["#NEW", "#A", "#B", "#C", "#D", "#E"], updated);   // 封顶 6，最老的 #F 掉出

        var dedup = ColorMath.UpdateRecent(updated, "#a");               // 与 "#A" 大小写不敏感重复
        Assert.Equal(["#a", "#NEW", "#B", "#C", "#D", "#E"], dedup);

        Assert.Empty(ColorMath.UpdateRecent(recent, "#X", cap: 0));
    }

    [Fact]
    public void 色板清单全部是合法且互不重复的六位色()
    {
        Assert.Equal(32, ColorMath.RecommendedAccentPalette.Length);
        Assert.All(ColorMath.RecommendedAccentPalette,
            c => Assert.True(ColorMath.TryParseHex(c, out _, out _, out _), c));
        Assert.Equal(ColorMath.RecommendedAccentPalette.Length,
            ColorMath.RecommendedAccentPalette.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
