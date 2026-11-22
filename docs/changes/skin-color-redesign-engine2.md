---
slug: skin-color-redesign-engine2
status: approved
date: 2026-11-23
tier: L
changed-files: src/WinQuick.Core/Design/ColorMath.cs, src/WinQuick.Core/Design/SkinCatalog.cs, src/WinQuick.Core/Design/DesignTokens.cs, src/WinQuick.App/Design/SystemSkin.cs, src/WinQuick.App/Design/TokenResources.cs
---

# 皮肤配色重设计 · engine 增量批：缩进修复 + 主批第 7–11 行

- 改什么：修复 engine 主批新内容首行缩进丢失（5 处）；补 SystemSkin（AccentLabelText→BestTextOn、删浅色手拍次要面）与 TokenResources（PanelPalette.AccentText、强调色兜底、资源键）。
- 为什么：主批执行中断（第 7 行锚点按旧行号错位），src/ 半态不可留；锚点行号已按当前文件状态核校。
- 影响面：编译面 App/Core；渲染接线与 JSON 数据仍属后续批。
- 回滚方式：编译自动快照 docs/changes/skin-color-redesign-engine2.rollback.json；--rollback 一键还原。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.Core/Design/ColorMath.cs | 102 | ]; | 1 |     ]; |
| 2 | src/WinQuick.Core/Design/SkinCatalog.cs | 111 | public string MergeWithOverrides | 1 |     public string MergeWithOverrides(string baseTokensJson, JsonObject overrides) |
| 3 | src/WinQuick.Core/Design/SkinCatalog.cs | 91 | public string Merge( | 1 |     public string Merge(string baseTokensJson, string? skinId) |
| 4 | src/WinQuick.Core/Design/DesignTokens.cs | 304 | public string HoverLabel | 1 |     public string HoverLabel { get; init; } = "#FFFFFF"; |
| 5 | src/WinQuick.Core/Design/DesignTokens.cs | 83 | if (Color.HoverFillAlpha | 1 |         if (Color.HoverFillAlpha is < 0 or > 1) |
| 6 | src/WinQuick.App/Design/SystemSkin.cs | 65 | 悬停字色 | 11 |     /// 悬停字色按强调色自动取黑/白 —— Win11 官方机制（强调色「自动为明暗两模式优化对比度」）。<NL>    /// 规则升级（spec skin-color-redesign R1）：旧的「线性亮度 > 0.5」硬阈值换成 ColorMath.BestTextOn<NL>    /// —— 取 WCAG 对比度更大的一侧。常见强调色两规则同结果，中间调（#7C4DFF 类）新规则站到可读的一侧；<NL>    /// 「暗态 + #0078D4 + 深字差 0.29 被校验拦下」那一类配错从此被恒达标取代。<NL>    /// </summary><NL>    private static string AccentLabelText(string accentHex) =><NL>        WinQuick.Core.Design.ColorMath.BestTextOn(accentHex);<NL>} |
| 7 | src/WinQuick.App/Design/SystemSkin.cs | 53 | ["deadZoneFill"] | 7 |                 ["label"] = "#1B1B1B",<NL>                ["hint"] = "#1B1B1B",<NL>                ["hintAlpha"] = 0.6, |
| 8 | src/WinQuick.App/Design/TokenResources.cs | 112 | Set("Color.Accent", TokenBrushes.Brush(panel.Accent)); | 1 |         Set("Color.Accent", TokenBrushes.Brush(panel.Accent));<NL>        // 选中行/高亮态的 on-accent 配对字色（spec R1；Material on-X）。<NL>        Set("Color.AccentText", TokenBrushes.Brush(panel.AccentText)); |
| 9 | src/WinQuick.App/Design/TokenResources.cs | 48 | return new PanelPalette( | 11 |         // 强调色兜底（spec R1 连带修正）：D 档玻璃的 HoverFill 是压暗黑（悬停语义），<NL>        // 与面板底不足 3:1 时它不再适合当「强调文字/指示线」，翻到可读一侧 —— 选中行成白底黑字，与原型挑定一致。<NL>        var accent = ColorMath.Contrast(c.HoverFill, surface) >= 3.0<NL>            ? c.HoverFill : ColorMath.BestTextOn(surface);<NL>        return new PanelPalette(<NL>            SurfaceSolid: surface,<NL>            Surface: surface,<NL>            FieldBackground: field,<NL>            Outline: light ? "#000000" : "#FFFFFF",<NL>            HoverSurface: light ? "#000000" : "#FFFFFF",<NL>            // 强调色取原色不透明：HoverFill 的 α 是「轮盘悬停高亮的半透明感」参数，<NL>            // 面板强调是实色语义 —— 按 α 合成会把 #33FF66/0.22 压成与底趋同的暗绿（SkinContrast 抓到）。<NL>            Accent: accent,<NL>            // on-accent 配对字色（spec R1）：选中行以 Accent 为底时文字用它 ——<NL>            // 半透明皮肤 HoverFill 与 Label 同为 #FFFFFF，复用 Color.Text 就是白底白字整行消失（用户截图实锤）。<NL>            AccentText: ColorMath.BestTextOn(accent),<NL>            Danger: light ? "#C42B1C" : "#F08A8A",<NL>            Success: light ? "#1B7F3A" : "#8CC88C"); |
| 10 | src/WinQuick.App/Design/TokenResources.cs | 26 | 面板派生键的结果 | 4 |     /// <summary>面板派生键的结果（纯数据，供 Install 与单测共用）。</summary><NL>    public sealed record PanelPalette(<NL>        string SurfaceSolid, string Surface, string FieldBackground,<NL>        string Outline, string HoverSurface, string Accent, string Danger, string Success,<NL>        string AccentText); |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| src/WinQuick.App/Design/SystemSkin.cs | ColorMath.BestTextOn(accentHex) |
| src/WinQuick.App/Design/TokenResources.cs | AccentText: ColorMath.BestTextOn(accent) |
| src/WinQuick.App/Design/TokenResources.cs | Color.AccentText |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-engine2.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```