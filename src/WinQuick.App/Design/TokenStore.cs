using System.IO;
using System.Reflection;
using System.Windows.Media;
using WinQuick.Core.Design;

namespace WinQuick.App.Design;

/// <summary>
/// 视觉规格的访问入口。tokens.json 以嵌入资源形式随程序发布并在这里解析一次。
///
/// 校验失败会直接抛异常而不是退化到默认值 —— 规格写错时应该立刻发现，
/// 而不是让界面悄悄用一组没人设计过的值。
/// </summary>
public static class TokenStore
{
    private const string ResourceName = "WinQuick.tokens.json";

    public static DesignTokens Current { get; } = Load();

    public static ColorTokens Colors => Current.Color;
    public static RadiusTokens Radii => Current.Radius;

    private static DesignTokens Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"找不到嵌入资源 {ResourceName}。检查 WinQuick.App.csproj 里的 EmbeddedResource 项。");

        using var reader = new StreamReader(stream);
        var tokens = DesignTokens.Parse(reader.ReadToEnd());

        var problems = tokens.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "design/tokens.json 未通过校验：\n  - " + string.Join("\n  - ", problems));
        }

        return tokens;
    }
}

/// <summary>
/// 令牌到 WPF 画刷的转换。
///
/// 令牌用 #RRGGBB + 单独 alpha 表达（便于和设计工具对齐），
/// 而 WPF 需要一个 <see cref="Color"/>，所以集中在这里换算 —— 散落各处的
/// 手写 Color.FromArgb 一旦和 JSON 不一致就会出现"改了规格没生效"。
/// </summary>
public static class TokenBrushes
{
    public static Color Parse(string hex, double alpha)
    {
        var rgb = hex.TrimStart('#');
        if (rgb.Length != 6)
            throw new InvalidOperationException($"颜色格式应为 #RRGGBB，实际是 {hex}");

        var r = Convert.ToByte(rgb[..2], 16);
        var g = Convert.ToByte(rgb.Substring(2, 2), 16);
        var b = Convert.ToByte(rgb.Substring(4, 2), 16);
        var a = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);

        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>冻结画刷：它们会被很多图形共享，冻结后 WPF 可跳过可变性检查。</summary>
    public static SolidColorBrush Brush(string hex, double alpha = 1.0)
    {
        var brush = new SolidColorBrush(Parse(hex, alpha));
        brush.Freeze();
        return brush;
    }
}
