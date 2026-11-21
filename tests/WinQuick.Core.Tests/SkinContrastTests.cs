using WinQuick.App.Design;
using WinQuick.Core.Design;

namespace WinQuick.Core.Tests;

/// <summary>
/// 皮肤可读性回归锁（spec skin-formalization §6）：每套皮肤合并后的令牌必须整体通过
/// DesignTokens.Validate（含新增的正文/次级文字对比度配对与过冲上限）。
/// 浅色皮肤的「内容显示问题」就是只查 hover 一对漏出来的 —— 这组测试保证任何一套皮肤
/// 改坏了配色都会在单测层红掉，而不是等用户在浅色下看见深色块。
/// 对照用例保证校验本身能失败（拦得住），不是恒真。
/// </summary>
public class SkinContrastTests
{
    public static IEnumerable<object[]> AllSkins()
    {
        foreach (var skin in TokenStore.Skins.All)
            yield return new object[] { skin.Id };
    }

    [Theory]
    [MemberData(nameof(AllSkins))]
    public void 每套皮肤合并令牌整体通过校验(string skinId)
    {
        var merged = TokenStore.PreviewTokens(skinId);
        var problems = merged.Validate();
        Assert.True(problems.Count == 0,
            $"皮肤「{skinId}」未过校验：{string.Join("；", problems)}");
    }

    [Fact]
    public void 校验能拦住低对比组合_对照()
    {
        // 白底白字：如果配对校验被删或阈值被放宽，这条会红 —— 保证上面的 Theory 不是恒真。
        var tokens = TokenStore.PreviewTokens(TokenStore.Skins.All[0].Id);
        var bad = tokens with { Color = tokens.Color with { Label = tokens.Color.SectorFill } };
        Assert.Contains(bad.Validate(), p => p.Contains("label"));
    }

    /// <summary>面板配对（skin-panel-readability）：面板是实色表面，派生键必须对每套皮肤可读。</summary>
    public static IEnumerable<object[]> PanelStates()
    {
        foreach (var skin in TokenStore.Skins.All)
            yield return new object[] { skin.Id };
        yield return new object[] { "system-light" };
        yield return new object[] { "system-dark" };
    }

    private static double Lum(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    [Theory]
    [MemberData(nameof(PanelStates))]
    public void 面板派生键_每套皮肤下正文与强调色都可读(string stateId)
    {
        // system 亮态用固定输入合成（P1：PreviewTokens(system) 读宿主注册表——非确定测试，
        // 本机暗色模式下浅色配对从未被覆盖；固定 true + 官方默认强调色即确定性覆盖）。
        var color = stateId switch
        {
            "system-light" => TokenStore.LoadWithOverrides(SystemSkin.Compose(true, "#0078D4")).Color,
            _ => TokenStore.PreviewTokens(stateId).Color,
        };
        var panel = TokenResources.DerivePanelColors(color);
        var surface = Lum(panel.SurfaceSolid);
        Assert.True(Math.Abs(surface - Lum(color.Label)) >= 0.3,
            $"{stateId}：正文与面板底亮度差不足（{panel.SurfaceSolid} vs {color.Label}）");
        Assert.True(Math.Abs(surface - Lum(panel.Accent)) >= 0.2,
            $"{stateId}：强调色与面板底亮度差不足（{panel.SurfaceSolid} vs {panel.Accent}）");
        Assert.True(Math.Abs(Lum(panel.FieldBackground) - Lum(color.Label)) >= 0.15,
            $"{stateId}：输入框底与正文亮度差不足（{panel.FieldBackground} vs {color.Label}）");
    }

    [Fact]
    public void 半透明皮肤的面板底是暗色_回归对照()
    {
        // 修复前：α0.07 白实色化=纯白底配白字。这条锁死合成语义不再回退。
        var merged = TokenStore.PreviewTokens("translucent");
        var panel = TokenResources.DerivePanelColors(merged.Color);
        Assert.True(Lum(panel.SurfaceSolid) < 0.3, $"应为暗面板，实为 {panel.SurfaceSolid}");
    }
}
