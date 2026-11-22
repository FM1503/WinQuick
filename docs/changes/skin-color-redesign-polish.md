---
slug: skin-color-redesign-polish
status: approved
date: 2026-11-23
tier: L
changed-files: src/WinQuick.App/Radial/SkinPanel.xaml, src/WinQuick.App/Design/TokenResources.cs, src/WinQuick.App/Design/SystemSkin.cs, design/skins.json, src/WinQuick.Core/Design/ColorMath.cs, src/WinQuick.Core/Design/SkinCatalog.cs, src/WinQuick.Core/Design/DesignTokens.cs, tests/WinQuick.Core.Tests/SkinCatalogTests.cs, docs/specs/skin-color-redesign.md
---

# 皮肤配色重设计 · polish 批：R5 标题 B 档落地 + 事故缩进清理 + spec 勘误

- 改什么：审查 P1（R5 B 档未落地：SkinPanel.xaml Section 标题仍用 Accent 当文字）——按用户原型定稿（默认 B 档：Text 色 + SemiBold）改为 Wq.Color.Text；清掉 compile-change 首行 trim 造成的 9 处 column-0 残留；spec 四处定稿/实现勘误（P2-1/2/3）。
- 为什么：pass-with-issues 的 P1 必须修；开源仓库的 diff 不留悬空缩进；文档说的和代码做的必须一致。
- 影响面：Section 标题颜色（跟随皮肤 Text 色，明暗皮肤都稳）；其余为格式与文档。
- 回滚方式：docs/changes/skin-color-redesign-polish.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/Radial/SkinPanel.xaml | 30 | Wq.Color.Accent | 1 | <NL>            <Setter Property="Foreground" Value="{DynamicResource Wq.Color.Text}" /> |
| 2 | src/WinQuick.App/Design/TokenResources.cs | 120 | Set("Color.Accent", TokenBrushes.Brush(panel.Accent)); | 1 | <NL>        Set("Color.Accent", TokenBrushes.Brush(panel.Accent)); |
| 3 | src/WinQuick.App/Design/SystemSkin.cs | 53 | ["label"] = "#1B1B1B", | 1 | <NL>                ["label"] = "#1B1B1B", |
| 4 | design/skins.json | 132 | deadZoneStrokeAlpha | 1 | <NL>          "deadZoneStrokeAlpha": 0.55 |
| 5 | design/skins.json | 17 | 新令牌（strokeWidth | 1 | <NL>    "新令牌（strokeWidth / halo* / hoverFillAlpha / hoverLabel / childStaggerMs）", |
| 6 | src/WinQuick.Core/Design/ColorMath.cs | 102 | ]; | 1 | <NL>    ]; |
| 7 | src/WinQuick.Core/Design/SkinCatalog.cs | 111 | public string MergeWithOverrides | 1 | <NL>    public string MergeWithOverrides(string baseTokensJson, JsonObject overrides) |
| 8 | src/WinQuick.Core/Design/SkinCatalog.cs | 91 | public string Merge( | 1 | <NL>    public string Merge(string baseTokensJson, string? skinId) |
| 9 | src/WinQuick.Core/Design/DesignTokens.cs | 304 | public string HoverLabel | 1 | <NL>    public string HoverLabel { get; init; } = "#FFFFFF"; |
| 10 | src/WinQuick.Core/Design/DesignTokens.cs | 83 | if (Color.HoverFillAlpha is | 1 | <NL>        if (Color.HoverFillAlpha is < 0 or > 1) |
| 11 | tests/WinQuick.Core.Tests/SkinCatalogTests.cs | 133 | [Fact] | 1 | <NL>    [Fact] |
| 12 | docs/specs/skin-color-redesign.md | 65 | R5 面板标题类 | 2 | R5 面板标题类「强调色当文字」处（Section 用 Color.Accent）：给出两档让用户挑<NL>   （达标保证的强调色标题 vs Text 色 + SemiBold），依据是微软明文不建议在 acrylic 面上放强调色文字。<NL>   **定稿（2026-11-23 用户原型确认，默认档）**：B 档 —— Section 标题用 Text 色 + SemiBold（SkinPanel.xaml），Accent 只保留「当线/当点缀」语义。 |
| 13 | docs/specs/skin-color-redesign.md | 58 | R2 半透明皮肤加 | 2 | R2 半透明皮肤加 **scrim 保底层**（Fluent Acrylic 的真实构成），白字在浅色/纯白壁纸上达标；<NL>   **定稿（2026-11-23 用户原型挑定）**：五档 A–E（0.45/0.55 提亮、0.65 提亮、0.65 压暗、0.72 压暗）中取 D 档 —— scrim α0.65 + 悬停压暗（hoverFill #000000 α0.18）。 |
| 14 | docs/specs/skin-color-redesign.md | 116 | 进 DesignTokens.Validate 域（0..1） | 1 | 进 DesignTokens.Validate 域（0..0.9 —— 实现时把上限从 1 收紧到 0.9，超过就不是玻璃是黑板；reviewer P2 勘误）。默认皮肤行为不变。 |
| 15 | docs/specs/skin-color-redesign.md | 143 | 页内 selfCheck() | 1 | - 页内 selfCheck()（onerror + 关键配对计算复核），`node preview/_verify.mjs` 可跑纯逻辑自检。<NL>  注（reviewer P2-3 勘误）：原型 selfCheck 的 W4/W5 按面板合成底口径，比单测口径乐观；单测按实际渲染分支（scrim 保底盘死区、角标叠扇区底）判定 —— 终判以单测与实机为准，两者当时皆过。 |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| docs/specs/skin-color-redesign.md | 用户原型挑定 |
| docs/specs/skin-color-redesign.md | B 档 —— Section 标题用 Text 色 |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-polish.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests -c Release --nologo
```