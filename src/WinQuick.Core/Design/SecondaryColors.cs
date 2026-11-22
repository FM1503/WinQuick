using System.Text.Json.Nodes;

namespace WinQuick.Core.Design;

/// <summary>
/// 轮盘次要表面的统一派生（spec skin-color-redesign R3）。
///
/// 缺陷来源（用户截图实锤）：死区/角标此前逐套手拍 —— 半透明皮肤手拍出不透明纯白死区盘，
/// 与半透扇区材质脱节；角标底与占位字之间没有任何保证关系。改为派生后「皮肤只定身份色，
/// 次要面自动跟材质」成为规则；皮肤仍可显式覆盖（保留高对比那圈实心白描边这类身份需求）。
/// 语义：派生基准 = sectorFill 按 α 合成到暗基线（与面板 DerivePanelColors 同源）。
/// scrim 皮肤的轮盘死区**实际渲染**走半透保底盘（见 RadialRingVisual），这里的回填值是
/// 无 scrim 分支的实色与单测判据 —— 两边语义都进配对矩阵。
/// </summary>
public static class SecondaryColors
{
    private const string DarkBase = "#202020";

    /// <summary>
    /// 对 merged（已应用覆盖的 color 节）中**未被显式覆盖**的次要面键回填派生值。
    /// covered = 皮肤覆盖过的键（小写）。回填发生在合并尾部，所以 ColorTokens 拿到的
    /// 就是最终值 —— 渲染层与单测都不需要知道「这个值是手拍还是派生」。
    /// </summary>
    public static void FillMissing(JsonObject? color, IReadOnlySet<string> covered)
    {
        if (color is null) return;

        var fill = color["sectorFill"]?.GetValue<string>() ?? "#202020";
        var alpha = color["sectorFillAlpha"]?.GetValue<double>() ?? 0.92;
        var surf = ColorMath.Over(fill, alpha, DarkBase);
        var light = ColorMath.Luminance(surf) > 0.5;   // 方向判定用线性亮度：与面板翻转判据同源

        Set("deadZoneFill", light ? ColorMath.Over("#FFFFFF", 0.10, surf)
                                  : ColorMath.Over("#000000", 0.35, surf));
        Set("badgeFill", light ? ColorMath.Over("#000000", 0.12, surf)
                               : ColorMath.Over("#000000", 0.55, surf));
        Set("badgeFillAlpha", 1.0);
        Set("deadZoneStroke", light ? "#000000" : "#FFFFFF");
        Set("deadZoneStrokeAlpha", light ? 0.5 : 0.35);

        void Set(string key, JsonNode value)
        {

            if (!covered.Contains(key.ToLowerInvariant())) color[key] = value;
        }
    }
}
