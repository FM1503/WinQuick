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

    // ==== skin-color-redesign（2026-11-23）：WCAG 严格比值全配对矩阵 ====
    // 上面的线性 Lum 差锁的是「方向翻转」；本节锁「达到可读阈值」——
    // 公式真源在 ColorMath，与 preview/skin-color-preview.html 页内 selfCheck 逐式同源。

    [Theory]
    [MemberData(nameof(PanelStates))]
    public void 全配对行_每套皮肤正文与大字都达WCAG阈值(string stateId)
    {
        var c = stateId switch
        {
            "system-light" => TokenStore.LoadWithOverrides(SystemSkin.Compose(true, "#0078D4")).Color,
            _ => TokenStore.PreviewTokens(stateId).Color,
        };
        var panel = TokenResources.DerivePanelColors(c);

        // 面板（实色表面）：正文 4.5、次级 3.0（大字级）、选中行文字-on-Accent 4.5（spec R1）。
        Assert.True(ColorMath.Contrast(c.Label, panel.SurfaceSolid) >= 4.5,
            $"{stateId} P1 正文↔面板底：{ColorMath.Contrast(c.Label, panel.SurfaceSolid):F2}");
        var muted = ColorMath.Over(c.Hint, 0.72, panel.SurfaceSolid);
        Assert.True(ColorMath.Contrast(muted, panel.SurfaceSolid) >= 3.0,
            $"{stateId} P2 TextMuted↔面板底：{ColorMath.Contrast(muted, panel.SurfaceSolid):F2}");
        Assert.True(ColorMath.Contrast(panel.AccentText, panel.Accent) >= 4.5,
            $"{stateId} P3 AccentText↔Accent（选中行）：{ColorMath.Contrast(panel.AccentText, panel.Accent):F2}");

        // 轮盘：按最坏情形 = 纯白壁纸判定（scrim 皮肤经保底层 —— spec R2 的「任意壁纸」语义）。
        var wall = ColorMath.Over(c.SectorScrim, c.SectorScrimAlpha, "#FFFFFF");
        var sector = ColorMath.Over(c.SectorFill, c.SectorFillAlpha, wall);
        Assert.True(ColorMath.Contrast(c.Label, sector) >= 4.5,
            $"{stateId} W1 标签↔扇区(白壁纸)：{ColorMath.Contrast(c.Label, sector):F2}");
        var hintOn = ColorMath.Over(c.Hint, c.HintAlpha, sector);
        Assert.True(ColorMath.Contrast(hintOn, sector) >= 3.0,
            $"{stateId} W2 提示↔扇区：{ColorMath.Contrast(hintOn, sector):F2}");
        var hoverOn = ColorMath.Over(c.HoverFill, c.HoverFillAlpha, sector);
        Assert.True(ColorMath.Contrast(c.HoverLabel, hoverOn) >= 4.5,
            $"{stateId} W3 悬停字↔悬停合成底：{ColorMath.Contrast(c.HoverLabel, hoverOn):F2}");

        // 角标（子项首字占位/spinner）与死区：scrim 皮肤的轮盘死区实际渲染是半透保底盘
        //（RadialRingVisual 的分支），不是回填的派生实色 —— 判据跟着实际渲染走。
        var badge = ColorMath.Over(c.BadgeFill, c.BadgeFillAlpha, sector);
        Assert.True(ColorMath.Contrast(c.Label, badge) >= 4.5,
            $"{stateId} W4 占位字↔角标底：{ColorMath.Contrast(c.Label, badge):F2}");
        var dead = c.SectorScrimAlpha > 0.001
            ? ColorMath.Over(c.SectorScrim, c.SectorScrimAlpha, "#FFFFFF")
            : c.DeadZoneFill;
        Assert.True(ColorMath.Contrast(c.Label, dead) >= 4.5,
            $"{stateId} W5 标签↔死区：{ColorMath.Contrast(c.Label, dead):F2}");

        // 非文本边界（SC 1.4.11）：死区描边至少与一侧相邻可辨（描边跨两侧，判两侧较大者 ≥3.0）。
        var edgeOnDead = ColorMath.Over(c.DeadZoneStroke, c.DeadZoneStrokeAlpha, dead);
        var edgeOnSector = ColorMath.Over(c.DeadZoneStroke, c.DeadZoneStrokeAlpha, sector);
        Assert.True(Math.Max(ColorMath.Contrast(edgeOnDead, sector),
                             ColorMath.Contrast(edgeOnSector, dead)) >= 3.0,
            $"{stateId} 死区描边与两侧相邻都不足 3:1");
    }

    [Theory]
    [InlineData("#FFFF00", "#1B1B1B")]
    [InlineData("#0078D4", "#FFFFFF")]
    [InlineData("#00E5FF", "#1B1B1B")]
    [InlineData("#33FF66", "#1B1B1B")]
    [InlineData("#D0BCFF", "#1B1B1B")]
    [InlineData("#B45309", "#FFFFFF")]
    [InlineData("#FFFFFF", "#1B1B1B")]
    [InlineData("#000000", "#FFFFFF")]
    public void on_accent择一_取对比度更大侧(string background, string expected) =>
        Assert.Equal(expected, ColorMath.BestTextOn(background));

    [Fact]
    public void 次要面派生_默认皮肤以确定值回填同族暗色死区与角标()
    {
        var c = TokenStore.PreviewTokens("default").Color;
        Assert.Equal("#151515", c.DeadZoneFill);
        Assert.Equal("#0E0E0E", c.BadgeFill);
        Assert.Equal(1.0, c.BadgeFillAlpha);
        Assert.Equal("#FFFFFF", c.DeadZoneStroke);
        Assert.Equal(0.35, c.DeadZoneStrokeAlpha);
    }

    [Fact]
    public void 次要面派生_玻璃死区不再纯白_缺陷回归锁()
    {
        // 修复前：手拍 deadZoneFill #FFFFFF → 半透扇区中央的焊死大白盘（用户截图）。
        var c = TokenStore.PreviewTokens("translucent").Color;
        Assert.NotEqual("#FFFFFF", c.DeadZoneFill);
        Assert.True(c.SectorScrimAlpha >= 0.6, "玻璃皮肤必须带 scrim 保底层（spec R2 D 档）");
        Assert.Equal("#000000", c.HoverFill);
    }

    [Fact]
    public void 玻璃面板强调色兜底翻到可读一侧()
    {
        // D 档 HoverFill 是压暗黑 —— 面板 Accent 照抄就暗底暗字；兜底翻白后选中行白底黑字（与原型一致）。
        var c = TokenStore.PreviewTokens("translucent").Color;
        var panel = TokenResources.DerivePanelColors(c);
        Assert.Equal("#FFFFFF", panel.Accent);
        Assert.Equal("#1B1B1B", panel.AccentText);
        Assert.True(ColorMath.Contrast(panel.Accent, panel.SurfaceSolid) >= 3.0);
    }

    [Fact]
    public void 校验拦住scrim范围外值()
    {
        var tokens = TokenStore.PreviewTokens("default");
        var bad = tokens with { Color = tokens.Color with { SectorScrimAlpha = 1.2 } };
        Assert.Contains(bad.Validate(), p => p.Contains("sectorScrimAlpha"));
    }
}
