using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinQuick.Core.Design;

namespace WinQuick.App.Design;

/// <summary>
/// 把 <see cref="DesignTokens"/> 装进 <see cref="Application.Resources"/>，供 XAML 用
/// <c>DynamicResource</c> 引用。
///
/// 为什么需要它：之前的设置界面在 XAML 里**硬编码**颜色（<c>#FF1F1F1F</c> 之类），
/// 于是它和轮盘的视觉语言各写一套，很快就长得不一样 —— 用户直接说"太丑了、风格不统一"。
/// 更根本的问题是这些硬编码值不受 tokens.json 约束：改设计基线时它们不会跟着变，
/// 也不会被单测的规格护栏覆盖到。
///
/// 装进 Application.Resources 而不是每个窗口各建一份，是为了让所有界面共用同一份值。
/// </summary>
public static class TokenResources
{
    /// <summary>资源键前缀，避免和 WPF 内置键冲突。</summary>
    private const string Prefix = "Wq.";

    private static bool _installed;

    /// <summary>面板派生键的结果（纯数据，供 Install 与单测共用）。</summary>
    public sealed record PanelPalette(
        string SurfaceSolid, string Surface, string FieldBackground,
        string Outline, string HoverSurface, string Accent, string Danger, string Success);

    /// <summary>
    /// 面板配色的全部派生逻辑（skin-panel-readability，2026-11-21 审计后重建）。
    ///
    /// 语义：轮盘扇区是「原料叠桌面」（半透明原料在桌面上是暗底白字），
    /// 面板是「自绘实色表面」—— 直接用原料会把 α0.07 的白实色化成纯白底配白字。
    /// 所以面板键一律**先按 α 合成到暗基线**再派生；Outline/HoverSurface 基于合成后的底
    /// 判明暗（修正此前按原料判定的顺序缺陷）。
    /// 纯函数无 WPF 依赖 —— 可读性配对由此可被单测直接锁住。
    /// </summary>
    public static PanelPalette DerivePanelColors(Core.Design.ColorTokens c)
    {
        const string DarkBase = "#202020";
        var surface = Composite(c.SectorFill, c.SectorFillAlpha, DarkBase);
        var light = Luminance(surface) > 0.5;
        // 输入框一档阶梯：混色而非乘法 —— 乘法在纯黑底（contrast）恒为 0，层次消失（reviewer P2）。
        // Mix(a,b,t)=a·t+b·(1-t)：目标色（深灰/亮灰）占 12%、底占 88% —— 参数序反了会变成近纯白（实测教训）。
        var field = light ? Mix("#3A3A3A", surface, 0.12) : Mix("#F0F0F0", surface, 0.12);
        return new PanelPalette(
            SurfaceSolid: surface,
            Surface: surface,
            FieldBackground: field,
            Outline: light ? "#000000" : "#FFFFFF",
            HoverSurface: light ? "#000000" : "#FFFFFF",
            // 强调色取原色不透明：HoverFill 的 α 是「轮盘悬停高亮的半透明感」参数，
            // 面板强调是实色语义 —— 按 α 合成会把 #33FF66/0.22 压成与底趋同的暗绿（SkinContrast 抓到）。
            Accent: c.HoverFill,
            Danger: light ? "#C42B1C" : "#F08A8A",
            Success: light ? "#1B7F3A" : "#8CC88C");
    }

    /// <summary>按 α 把颜色合成到基色上（面板实色表面的语义核心）。</summary>
    private static string Composite(string hex, double alpha, string overHex)
    {
        var a = Math.Clamp(alpha, 0, 1);
        return Mix(hex, overHex, a);
    }

    private static string Mix(string aHex, string bHex, double t)
    {
        var (ar, ag, ab) = Rgb(aHex);
        var (br, bg, bb) = Rgb(bHex);
        var r = (int)Math.Round(ar * t + br * (1 - t));
        var g = (int)Math.Round(ag * t + bg * (1 - t));
        var b = (int)Math.Round(ab * t + bb * (1 - t));
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static (int, int, int) Rgb(string hex)
    {
        var body = hex.TrimStart('#');
        return (Convert.ToInt32(body[..2], 16), Convert.ToInt32(body.Substring(2, 2), 16), Convert.ToInt32(body.Substring(4, 2), 16));
    }

    /// <summary>
    /// 安装到应用资源。<paramref name="force"/> 为真时**重新**装一遍（换皮肤用）。
    ///
    /// 之所以要 force：所有界面都用 <c>DynamicResource</c> 引用令牌，
    /// 所以换皮肤时只要把资源重写一遍，面板的颜色与字号就跟着变了 ——
    /// 不必重建窗口（重建窗口＝重建表面＝闪）。
    /// </summary>
    public static void Install(bool force = false)
    {
        if (_installed && !force) return;
        _installed = true;

        var app = Application.Current;
        if (app is null) return;

        var tokens = TokenStore.Current;
        var color = tokens.Color;
        var radius = tokens.Radius;
        var typography = tokens.Typography;
        var motion = tokens.Motion;

        void Set(string key, object value) => app.Resources[Prefix + key] = value;

        // ---- 颜色 ----
        var panel = DerivePanelColors(color);
        Set("Color.Surface", TokenBrushes.Brush(panel.Surface));
        Set("Color.SurfaceSolid", TokenBrushes.Brush(panel.SurfaceSolid));
        Set("Color.Stroke", TokenBrushes.Brush(color.SectorStroke, color.SectorStrokeAlpha));
        Set("Color.Accent", TokenBrushes.Brush(panel.Accent));
        Set("Color.AccentStroke", TokenBrushes.Brush(color.HoverStroke, color.HoverStrokeAlpha));
        Set("Color.Text", TokenBrushes.Brush(color.Label));
        Set("Color.TextMuted", TokenBrushes.Brush(color.Hint, 0.72));
        Set("Color.FieldBackground", TokenBrushes.Brush(panel.FieldBackground));
        Set("Color.DeadZone", TokenBrushes.Brush(color.DeadZoneFill));
        Set("Color.DeadZoneStroke", TokenBrushes.Brush(color.DeadZoneStroke, color.DeadZoneStrokeAlpha));
        // 角标底色：SystemLocationPicker 无图标项的占位块用，与轮盘角标同源、换肤跟随。
        Set("Color.BadgeFill", TokenBrushes.Brush(color.BadgeFill, color.BadgeFillAlpha));

        // 派生键来自 DerivePanelColors（基于合成底的明暗翻转，含半透明皮肤的正确方向）。
        Set("Color.Outline", TokenBrushes.Brush(panel.Outline, 0.16));
        Set("Color.HoverSurface", TokenBrushes.Brush(panel.HoverSurface, 0.05));

        // 状态色：成功/警告。不放进 tokens —— 它们是语义色，与设计基线无关。
        Set("Color.Success", TokenBrushes.Brush(panel.Success));
        Set("Color.Danger", TokenBrushes.Brush(panel.Danger));

        // ---- 尺寸 ----
        Set("Radius.Window", (double)radius.WindowSize);
        Set("Size.CornerRadius", new CornerRadius(8));
        Set("Size.ControlCornerRadius", new CornerRadius(4));
        Set("Size.Padding", new Thickness(14));
        Set("Size.FieldPadding", new Thickness(6, 4, 6, 4));

        // ---- 字体 ----
        // 页脚占位高度：两行小字。目的是让窗口高度**不随状态文本的行数变化** ——
        // 面板是 SizeToContent="Height"，文本一变行数窗口就改高度，
        // 而分层窗口改高度会重建整块表面（用户看到的就是每次操作闪一下）。
        Set("Size.FooterMinHeight", (typography.LabelFontSize - 2.5) * 1.35 * 2 + 4);

        Set("Font.Family", new FontFamily(typography.LabelFontFamily));
        Set("Font.LabelSize", typography.LabelFontSize);
        Set("Font.BodySize", typography.LabelFontSize - 1);
        Set("Font.SmallSize", typography.LabelFontSize - 2.5);

        // ---- 动效 ----
        Set("Motion.Duration", TimeSpan.FromMilliseconds(motion.EntranceDurationMs));
        Set("Motion.HoverDuration", TimeSpan.FromMilliseconds(motion.HoverHighlightMs));
        Set("Motion.Easing", BuildKeySpline(motion.EntranceEasing));
    }

    /// <summary>按键取资源，取不到就抛 —— 静默退回默认值会让样式悄悄分叉。</summary>
    public static T Get<T>(string key)
    {
        var value = Application.Current?.Resources[Prefix + key];
        if (value is T typed) return typed;
        throw new InvalidOperationException($"资源 {Prefix}{key} 不存在或类型不符（实际 {value?.GetType().Name ?? "null"}）");
    }

    public static Brush Brush(string key) => Get<Brush>(key);

    private static KeySpline BuildKeySpline(string easing)
    {
        if (!MotionTokens.TryParseEasing(easing, out var p)) p = (0, 0, 0, 1);
        return new KeySpline(new Point(p.X1, p.Y1), new Point(p.X2, p.Y2));
    }

    /// <summary>Rec.709 相对亮度（与 DesignTokens.Validate 的判据同源，仅此处为派生键服务）。</summary>
    private static double Luminance(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }
}
