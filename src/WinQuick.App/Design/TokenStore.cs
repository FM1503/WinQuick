using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using WinQuick.Core.Design;

namespace WinQuick.App.Design;

/// <summary>
/// 视觉规格的访问入口。tokens.json（基线）与 skins.json（皮肤目录）都以嵌入资源形式
/// 随程序发布，在这里解析。
///
/// 校验失败会直接抛异常而不是退化到默认值 —— 规格写错时应该立刻发现，
/// 而不是让界面悄悄用一组没人设计过的值。
///
/// ## 皮肤是「合并」出来的，不是「第二份规格」
///
/// <see cref="Current"/> 永远是"基线 + 当前皮肤覆盖"的合并结果，并且**合并后仍然跑
/// 同一套 <see cref="DesignTokens.Validate"/>**。所以皮肤不可能绕开被单测锁住的任何不变量。
/// 切换皮肤＝重新合并 + 重新解析 + 重新装资源，不涉及任何"两份真源"的同步。
/// </summary>
public static class TokenStore
{
    private const string TokensResource = "WinQuick.tokens.json";
    private const string SkinsResource = "WinQuick.skins.json";

    private static readonly string BaseJson;
    private static DesignTokens _current;

    /// <summary>
    /// 静态构造里**显式按顺序**赋值。
    ///
    /// 不能靠字段初始化器的书写顺序：`_current` 的初始化要读 <see cref="Skins"/>，
    /// 而 `Skins` 的初始化里要读 <see cref="BaseJson"/> —— 三者有依赖，
    /// 而初始化器按**声明顺序**执行。写成字段初始化器时我把 Skins 声明在了 _current 之后，
    /// 结果整个类型初始化抛 NullReferenceException（--render-preview 一跑就炸）。
    /// 显式构造器让这个依赖关系一眼可见，编译器也不会再替我们决定顺序。
    /// </summary>
    static TokenStore()
    {
        BaseJson = ReadEmbedded(TokensResource);
        Skins = SkinCatalog.Parse(ReadEmbedded(SkinsResource));
        CurrentSkinId = SkinCatalog.DefaultId;
        _current = Load(null);
    }

    /// <summary>当前生效的规格（基线 + 当前皮肤）。</summary>
    public static DesignTokens Current => _current;

    /// <summary>当前皮肤 id。<see cref="SkinCatalog.DefaultId"/> 表示就是基线本身。</summary>
    public static string CurrentSkinId { get; private set; }

    /// <summary>皮肤目录（面板用它列出可选皮肤）。</summary>
    public static SkinCatalog Skins { get; }

    public static ColorTokens Colors => Current.Color;
    public static RadiusTokens Radii => Current.Radius;

    /// <summary>
    /// 切换皮肤。返回问题描述（null = 成功）。
    ///
    /// **不抛异常**：配置里写了一个不存在的皮肤 id（比如用户手改过 config.json、
    /// 或者皮肤文件被删了）不该让程序起不来 —— 退回默认皮肤，并把这件事报给调用方显示。
    /// </summary>
    public static string? ApplySkin(string? skinId)
    {
        string? problem = null;
        // system 不在静态目录（overrides 是运行时合成的），校验与装配都要走特判，
        // 否则会被误报成「没有名为 system 的皮肤」并退回默认。
        if (!string.IsNullOrWhiteSpace(skinId) && skinId != SystemSkin.Id && !Skins.Contains(skinId))
        {
            problem = $"没有名为「{skinId}」的皮肤，已退回默认皮肤";
            skinId = SkinCatalog.DefaultId;
        }

        var next = skinId == SystemSkin.Id
            ? LoadWithOverrides(SystemSkin.Compose(SystemThemeWatcher.IsLightMode(), SystemThemeWatcher.AccentColor()))
            : Load(skinId);

        CurrentSkinId = Skins.Contains(skinId) ? skinId! : SkinCatalog.DefaultId;
        if (skinId == SystemSkin.Id) CurrentSkinId = SystemSkin.Id; // Contains 认不出 system，在其后修正
        _current = next;

        // 面板与托盘那侧的资源要跟着换。XAML 全部走 DynamicResource，所以一次重装就够。
        TokenResources.Install(force: true);

        return problem;
    }

    /// <summary>
    /// 只看不换：算出某套皮肤合并后的令牌（面板用它画色板与动效摘要）。
    ///
    /// 为什么不做"预览色好漂亮"的独立配色表：那份表迟早和真效果不一致，
    /// 而用户正是要拿这些色板做选择。
    /// </summary>
    public static DesignTokens PreviewTokens(string? skinId) =>
        skinId == SystemSkin.Id
            ? LoadWithOverrides(SystemSkin.Compose(SystemThemeWatcher.IsLightMode(), SystemThemeWatcher.AccentColor()))
            : DesignTokens.Parse(Skins.Merge(BaseJson, skinId));

    /// <summary>
    /// 合并运行时合成的 overrides 并校验（「跟随系统」皮肤入口，spec §5）。
    /// 即 Load 的「拿 Overrides 合并」那半段接受任意 JsonObject —— 走同一套
    /// Parse + Validate，动态合成的皮肤与静态皮肤受同一组不变量约束。
    /// </summary>
    internal static DesignTokens LoadWithOverrides(System.Text.Json.Nodes.JsonObject overrides)
    {
        var tokens = DesignTokens.Parse(Skins.MergeWithOverrides(BaseJson, overrides));

        var problems = tokens.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{TokensResource}（皮肤：{SystemSkin.Id}）未通过校验：\n  - " +
                string.Join("\n  - ", problems));
        }

        return tokens;
    }

    private static DesignTokens Load(string? skinId)
    {
        var tokens = DesignTokens.Parse(Skins.Merge(BaseJson, skinId));

        var problems = tokens.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{TokensResource}（皮肤：{skinId ?? SkinCatalog.DefaultId}）未通过校验：\n  - " +
                string.Join("\n  - ", problems));
        }

        return tokens;
    }

    private static string ReadEmbedded(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"找不到嵌入资源 {name}。检查 WinQuick.App.csproj 里的 EmbeddedResource 项。");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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

    /// <summary>
    /// 冻结画刷：它们会被很多图形共享，冻结后 WPF 可跳过可变性检查。
    ///
    /// **按 (颜色, 透明度) 缓存。** 每次调用都新建一个的话，悬停切换一次就要造好几个
    /// 画刷 —— 而悬停是轮盘上最频繁的动作，每次切换都在给 GC 送垃圾。
    /// 画刷本身是冻结的、不可变的，共享完全安全。
    /// </summary>
    public static SolidColorBrush Brush(string hex, double alpha = 1.0)
    {
        var key = (hex, alpha);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var brush = new SolidColorBrush(Parse(hex, alpha));
        brush.Freeze();
        Cache[key] = brush;
        return brush;
    }

    private static readonly Dictionary<(string Hex, double Alpha), SolidColorBrush> Cache = [];
}
