namespace WinQuick.Core.Design;

/// <summary>
/// 高亮色调色盘用的纯颜色算术：HSV ↔ RGB ↔ hex、最近使用列表的更新、预设色板清单。
///
/// 为什么放 Core：HSV 拖拽的换算、「#00 这种半截值不能生效」这类规则，
/// 错了的表现都是"选出来的颜色不对"，靠肉眼比对很难发现；
/// 抽成纯函数可以用已知颜色对（红、Win11 蓝等）逐条锁死。
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// HSV → RGB。h ∈ [0,360)，s、v ∈ [0,1]。越界值按环回/钳制处理，不抛。
    /// </summary>
    public static (int R, int G, int B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);

        // 每通道独立算的标准写法（W3C CSS Color 3 的 HSV→RGB 公式）：
        // f(n) = V − V·S·max(0, min(k, 4−k, 1))，k = (n + H/60) mod 6。
        // 比 switch 六扇区少一层手写边界，也就少一个错边界的机会。
        double Channel(double n)
        {
            var k = (n + h / 60.0) % 6.0;
            if (k < 0) k += 6.0;
            return v - v * s * Math.Max(0.0, Math.Min(Math.Min(k, 4.0 - k), 1.0));
        }

        return ((int)Math.Round(Channel(5) * 255),
                (int)Math.Round(Channel(3) * 255),
                (int)Math.Round(Channel(1) * 255));
    }

    /// <summary>RGB → HSV。r/g/b ∈ [0,255]。</summary>
    public static (double H, double S, double V) RgbToHsv(int r, int g, int b)
    {
        var rf = Math.Clamp(r, 0, 255) / 255.0;
        var gf = Math.Clamp(g, 0, 255) / 255.0;
        var bf = Math.Clamp(b, 0, 255) / 255.0;

        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;

        double h;
        if (delta < 1e-9) h = 0;
        else if (max == rf) h = 60.0 * (((gf - bf) / delta) % 6.0);
        else if (max == gf) h = 60.0 * ((bf - rf) / delta + 2.0);
        else h = 60.0 * ((rf - gf) / delta + 4.0);
        if (h < 0) h += 360.0;

        return (h, max <= 0 ? 0 : delta / max, max);
    }

    /// <summary>RGB → #RRGGBB（大写）。</summary>
    public static string ToHex(int r, int g, int b) =>
        $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>
    /// 解析 #RRGGBB（# 可省略）。**六位完整才算数** ——「#00」这种半截值
    /// 在这里被拒，调用方据此做到「不完整输入不生效」。
    /// </summary>
    public static bool TryParseHex(string? text, out int r, out int g, out int b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var body = text.Trim().TrimStart('#');
        if (body.Length != 6 || !body.All(Uri.IsHexDigit)) return false;

        r = Convert.ToInt32(body[..2], 16);
        g = Convert.ToInt32(body.Substring(2, 2), 16);
        b = Convert.ToInt32(body.Substring(4, 2), 16);
        return true;
    }

    /// <summary>
    /// 最近使用列表的更新：新的顶到最前、去重、截到 <paramref name="cap"/> 个。
    /// </summary>
    public static IReadOnlyList<string> UpdateRecent(IReadOnlyList<string> recent, string hex, int cap = 6)
    {
        if (cap <= 0) return [];
        return new[] { hex }
            .Concat(recent.Where(c => !string.Equals(c, hex, StringComparison.OrdinalIgnoreCase)))
            .Take(cap)
            .ToList();
    }

    /// <summary>
    /// 预设色板（与 preview/colorpicker-preview.html 里用户实操过的一致）。
    /// 横向按色相走（红→黄→绿→青→蓝→紫→品红），末两行是彩灰与中性灰阶。
    /// 取的是 Win11 强调色一系的常用值，够覆盖日常选择，不追求色彩学完备。
    /// </summary>
    public static readonly string[] RecommendedAccentPalette =
    [
        "#E81123", "#EA5623", "#F7630C", "#CA5010", "#DA3B01", "#C30052", "#E3008C", "#BF0077",
        "#FFB900", "#FFF100", "#BCD917", "#6CCB5F", "#107C10", "#00B294", "#0099BC", "#0078D4",
        "#C7E0F4", "#69797E", "#4A5459", "#2D72BC", "#8764B8", "#744DA9", "#B146C2", "#E43CC7",
        "#0E1420", "#202020", "#3B3B3B", "#5D5D5D", "#8A8A8A", "#B3B3B3", "#D6D6D6", "#FFFFFF",

    ];

    // ==== 皮肤配对法（spec skin-color-redesign 2026-11-23）====
    // preview/skin-color-preview.html 页内 JS 与这几条逐式同源；产品派生、面板与单测共用。

    /// <summary>hex 以 alpha 叠到 baseHex 上：结果 = hex·α + baseHex·(1−α)。参数序在 v1.1.5 踩反过一次，以本注释为唯一语义。</summary>
    public static string Over(string hex, double alpha, string baseHex)
    {
        var a = Math.Clamp(alpha, 0, 1);
        if (!TryParseHex(hex, out var r1, out var g1, out var b1)) return baseHex;
        if (!TryParseHex(baseHex, out var r2, out var g2, out var b2)) return hex;
        return ToHex((int)Math.Round(r1 * a + r2 * (1 - a)),
                     (int)Math.Round(g1 * a + g2 * (1 - a)),
                     (int)Math.Round(b1 * a + b2 * (1 - a)));
    }

    /// <summary>线性 Rec.709 亮度（0..1）。只用于明暗方向判定 —— 与既有面板派生的翻转阈值同源，不换判据。</summary>
    public static double Luminance(string hex)
    {
        TryParseHex(hex, out var r, out var g, out var b);
        return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
    }

    /// <summary>WCAG 2.2 相对亮度（piecewise sRGB 解码）。配对阈值判据用它，不用线性版。</summary>
    public static double RelativeLuminance(string hex)
    {
        TryParseHex(hex, out var r, out var g, out var b);
        static double F(int v)
        {
            var c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * F(r) + 0.7152 * F(g) + 0.0722 * F(b);
    }

    /// <summary>WCAG 对比度比值（1..21）。判定不四舍五入 —— SC 1.4.11：2.999 即不合格。</summary>
    public static double Contrast(string a, string b)
    {
        var l1 = RelativeLuminance(a);
        var l2 = RelativeLuminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>
    /// on-color（Material 角色配对思想，spec S5）：画在 background 上的文字，
    /// 在 #1B1B1B / #FFFFFF 里取 WCAG 对比度更大的一侧。取代旧的「线性亮度 > 0.5」硬阈值 ——
    /// 常见强调色两规则同结果，中间调（如 #7C4DFF）新规则站到可读的一侧。
    /// </summary>
    public static string BestTextOn(string background) =>
        Contrast(background, "#1B1B1B") >= Contrast(background, "#FFFFFF") ? "#1B1B1B" : "#FFFFFF";
}
