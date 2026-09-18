using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinQuick.Core.Config;

/// <summary>
/// 用户配置（<c>config.json</c>）。
///
/// **与 design/tokens.json 的分工必须清楚**：
/// - tokens.json = 设计基线。配色、半径、时长、缓动曲线由它定义，属于"我作为开发者定的"。
/// - config.json = 用户偏好。只暴露少数几个用户真正会调的旋钮，默认值一律为 null，
///   表示"沿用设计基线"，只有显式设了才覆盖。
///
/// 这样分层是为了避免"同一个值有两个来源"—— 那正是我在 M2 刻意规避的双真源问题：
/// 两处都能改同一个值时，改了 A 没改 B 会静默分叉，而分叉要到界面看起来不对才发现。
/// </summary>
public sealed record AppConfig
{
    public int Version { get; init; } = 1;

    /// <summary>全局热键，例如 <c>"Ctrl+Win+W"</c>。空值表示用默认热键。</summary>
    public string? Hotkey { get; init; }

    /// <summary>
    /// RegisterHotKey 失败时是否允许降级到低级键盘钩子。
    /// 关掉它意味着热键被占用时直接报错，而不会去抢键（抢键会影响系统原有功能）。
    /// </summary>
    public bool AllowHookFallback { get; init; } = true;

    [JsonPropertyName("appearance")] public AppearanceConfig Appearance { get; init; } = new();
    [JsonPropertyName("behavior")] public BehaviorConfig Behavior { get; init; } = new();
    [JsonPropertyName("startup")] public StartupConfig Startup { get; init; } = new();

    /// <summary>
    /// 是否把「WinQuick → 添加 → 目的地」写进文件/文件夹的右键菜单（HKCU，不需要管理员）。
    ///
    /// 默认开：这是用户要的功能。关掉时会把注册表项**彻底删掉**而不是留着不管 ——
    /// 留着一个指向不存在程序的菜单项，比没有这个功能更糟。
    /// </summary>
    [JsonPropertyName("shellMenu")] public bool ShellMenu { get; init; } = true;

    /// <summary>启动时激活哪一份菜单。</summary>
    public string? ActiveMenu { get; init; }

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();

    /// <summary>校验。返回问题列表，空表示通过。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        // 热键必须能解析 —— 否则用户改了配置却毫无反应，且不知道为什么。
        if (!string.IsNullOrWhiteSpace(Hotkey) &&
            !WinQuick.Core.Hotkey.HotkeySpec.TryParse(Hotkey, out _, out var hotkeyError))
        {
            problems.Add($"hotkey “{Hotkey}” 无法解析：{hotkeyError}");
        }

        // 遮罩透明度是叠加在屏幕上的黑，超出 0-1 没有意义。
        if (Appearance.DimOpacity is { } dim && (dim < 0 || dim > 0.95))
            problems.Add($"appearance.dimOpacity={dim} 超出合理范围 0–0.95");

        // 悬停延迟：区间来自 Cockburn IJHCS 2024 的实测。
        // 低于 150ms 会明显误展开；高于 600ms 用户会感到迟滞。
        if (Behavior.HoverOpenDelayMs is { } open && (open < 0 || open > 600))
            problems.Add($"behavior.hoverOpenDelayMs={open} 超出合理范围 0–600");

        if (Behavior.HoverCloseDelayMs is { } close && (close < 0 || close > 1200))
            problems.Add($"behavior.hoverCloseDelayMs={close} 超出合理范围 0–1200");

        // 收起必须慢于展开，否则指针掠过扇区边缘时子菜单会闪。
        if (Behavior.HoverOpenDelayMs is { } o && Behavior.HoverCloseDelayMs is { } c && c <= o)
            problems.Add($"behavior 收起延迟（{c}）必须大于展开延迟（{o}），否则掠过边缘时子菜单会闪");

        if (Appearance.AccentColor is { } accent && !LooksLikeHexColor(accent))
            problems.Add($"appearance.accentColor “{accent}” 不是 #RRGGBB 格式");

        return problems;
    }

    private static bool LooksLikeHexColor(string text)
    {
        var body = text.TrimStart('#');
        return body.Length == 6 && body.All(Uri.IsHexDigit);
    }
}

/// <summary>
/// 外观覆盖项。**全部可空**：null 表示沿用 design/tokens.json 里的设计基线。
/// </summary>
public sealed record AppearanceConfig
{
    /// <summary>全屏遮罩的不透明度（0–1）。null = 用设计值。</summary>
    public double? DimOpacity { get; init; }

    /// <summary>悬停高亮色（#RRGGBB）。null = 用设计值。</summary>
    public string? AccentColor { get; init; }

    /// <summary>
    /// 当前皮肤 id（见 design/skins.json）。null/空 = 默认皮肤（就是基线本身）。
    /// 皮肤是合并到基线上的一层覆盖：它带材质（配色/描边/光晕/字体）与**动效**。
    /// </summary>
    public string? Skin { get; init; }

    /// <summary>
    /// 当前盘面形态（<c>pie</c> / <c>rounded</c> / <c>petal</c> / <c>dial</c>）。
    /// 形态与皮肤是**正交**的两个维度：皮肤换材质与动效，形态换单元的几何外形。
    /// null/空 = <c>pie</c>（现状）。
    /// </summary>
    public string? Form { get; init; }
}

public sealed record BehaviorConfig
{
    public int? HoverOpenDelayMs { get; init; }
    public int? HoverCloseDelayMs { get; init; }

    /// <summary>是否播放动画。null = 跟随系统「在 Windows 中显示动画」的无障碍设置。</summary>
    public bool? AnimationsEnabled { get; init; }

    /// <summary>失去焦点时是否关闭覆盖层。</summary>
    public bool HideOnFocusLoss { get; init; } = true;
}

public sealed record StartupConfig
{
    /// <summary>是否开机自启。首次运行会按它写入 HKCU Run。</summary>
    public bool AutoStart { get; init; }
}
