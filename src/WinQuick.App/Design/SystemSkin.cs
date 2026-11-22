namespace WinQuick.App.Design;

/// <summary>
/// 「跟随系统」皮肤的运行时合成（spec §5）：不落 JSON 文件 —— 系统明暗是运行时事实，
/// 写死在 skins.json 就成了第二份真源。键集合参照 light 皮肤（明）与默认基线（暗），
/// 强调色映射到 hoverFill（Fluent：强调色用于交互态）。
/// </summary>
public static class SystemSkin
{
    public const string Id = "system";

    public static Core.Design.SkinSpec Spec() => new()
    {
        Id = Id,
        Name = "跟随系统",
        Tagline = "明暗与强调色跟 Windows 走（动态）",
        Cost = "运行时合成，无 JSON",
        Src = "出处：Win11 官方「应用默认跟随用户主题与强调色」（learn.microsoft.com/windows/apps/develop/ui/theming）",
        Overrides = null, // 动态合成见 Compose，静态目录里只挂名
    };

    /// <summary>按当前系统状态合成 overrides（与 light/暗基线同一键集，accent 映射 hoverFill）。</summary>
    public static System.Text.Json.Nodes.JsonObject Compose(bool light, string accentHex)
    {
        if (!light)
        {
            // 暗色=基线本身 + 强调色
            return new System.Text.Json.Nodes.JsonObject
            {
                ["color"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["hoverFill"] = accentHex,
                    ["hoverLabel"] = AccentLabelText(accentHex),
                },
            };
        }

        var accent = accentHex;
        return new System.Text.Json.Nodes.JsonObject
        {
            ["color"] = new System.Text.Json.Nodes.JsonObject
            {
                ["dimAlpha"] = 0.28,
                ["sectorFill"] = "#F3F3F3",
                ["sectorFillAlpha"] = 0.94,
                ["sectorStroke"] = "#000000",
                ["sectorStrokeAlpha"] = 0.08,
                ["hoverFill"] = accent,
                ["hoverFillAlpha"] = 1.0,
                ["hoverStroke"] = "#FFFFFF",
                ["hoverStrokeAlpha"] = 0.9,
                ["hoverLabel"] = AccentLabelText(accent),

                ["label"] = "#1B1B1B",
                ["hint"] = "#1B1B1B",
                ["hintAlpha"] = 0.6,
            },
        };
    }

    /// <summary>

    /// 悬停字色按强调色自动取黑/白 —— Win11 官方机制（强调色「自动为明暗两模式优化对比度」）。
    /// 规则升级（spec skin-color-redesign R1）：旧的「线性亮度 > 0.5」硬阈值换成 ColorMath.BestTextOn
    /// —— 取 WCAG 对比度更大的一侧。常见强调色两规则同结果，中间调（#7C4DFF 类）新规则站到可读的一侧；
    /// 「暗态 + #0078D4 + 深字差 0.29 被校验拦下」那一类配错从此被恒达标取代。
    /// </summary>
    private static string AccentLabelText(string accentHex) =>
        WinQuick.Core.Design.ColorMath.BestTextOn(accentHex);
}
