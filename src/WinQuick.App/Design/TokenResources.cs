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
        Set("Color.Surface", TokenBrushes.Brush(color.SectorFill, color.SectorFillAlpha));
        Set("Color.SurfaceSolid", TokenBrushes.Brush(color.SectorFill));
        Set("Color.Stroke", TokenBrushes.Brush(color.SectorStroke, color.SectorStrokeAlpha));
        Set("Color.Accent", TokenBrushes.Brush(color.HoverFill));
        Set("Color.AccentStroke", TokenBrushes.Brush(color.HoverStroke, color.HoverStrokeAlpha));
        Set("Color.Text", TokenBrushes.Brush(color.Label));
        Set("Color.TextMuted", TokenBrushes.Brush(color.Hint, 0.72));
        Set("Color.FieldBackground", TokenBrushes.Brush(color.DeadZoneFill));
        Set("Color.DeadZone", TokenBrushes.Brush(color.DeadZoneFill));
        Set("Color.DeadZoneStroke", TokenBrushes.Brush(color.DeadZoneStroke, color.DeadZoneStrokeAlpha));

        // 状态色：成功/警告。不放进 tokens —— 它们是语义色，与设计基线无关。
        Set("Color.Success", new SolidColorBrush(Color.FromRgb(0x8C, 0xC8, 0x8C)));
        Set("Color.Danger", new SolidColorBrush(Color.FromRgb(0xF0, 0x8A, 0x8A)));

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
}
