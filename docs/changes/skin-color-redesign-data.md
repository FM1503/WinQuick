---
slug: skin-color-redesign-data
status: approved
date: 2026-11-23
tier: L
changed-files: design/tokens.json, design/skins.json
---

# 皮肤配色重设计 · data 批：scrim 落值与皮肤配色修订

- 改什么：tokens.json 增 sectorScrim/sectorScrimAlpha 基线键；skins.json 半透明落 D 档（scrim 0.65 + 悬停压暗）、八套删手拍 deadZone*/badge*（改走派生回填）、玻璃 tagline/cost/src 更新出处、_comment 补派生键说明。
- 为什么：spec R2/R3/R4，用户原型挑定 D 档 + 统一派生。
- 影响面：皮肤合并值（回填引擎已在 engine 批就位）；渲染消费在 wire 批。
- 回滚方式：docs/changes/skin-color-redesign-data.rollback.json；--rollback 一键还原。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | design/skins.json | 245 | "color": { | 19 | <NL>        "color": {<NL>          "dimAlpha": 0.5,<NL>          "sectorFill": "#0D140D",<NL>          "sectorFillAlpha": 1.0,<NL>          "sectorStroke": "#33FF66",<NL>          "sectorStrokeAlpha": 0.55,<NL>          "strokeWidth": 1.5,<NL>          "hoverFill": "#33FF66",<NL>          "hoverFillAlpha": 0.22,<NL>          "hoverStroke": "#33FF66",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#33FF66",<NL>          "label": "#5CFF8E",<NL>          "hintAlpha": 0.8<NL>        }, |
| 2 | design/skins.json | 200 | "color": { | 19 | <NL>        "color": {<NL>          "dimAlpha": 0.72,<NL>          "sectorFill": "#000000",<NL>          "sectorFillAlpha": 1.0,<NL>          "sectorStroke": "#FFFFFF",<NL>          "sectorStrokeAlpha": 1.0,<NL>          "strokeWidth": 2.0,<NL>          "hoverFill": "#FFFF00",<NL>          "hoverFillAlpha": 1.0,<NL>          "hoverStroke": "#FFFFFF",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#000000",<NL>          "deadZoneStrokeAlpha": 1.0,<NL>          "label": "#FFFFFF",<NL>          "hintAlpha": 0.9<NL>        }, |
| 3 | design/skins.json | 154 | "color": { | 18 | <NL>        "color": {<NL>          "dimAlpha": 0.32,<NL>          "sectorFill": "#1C1B1F",<NL>          "sectorFillAlpha": 1.0,<NL>          "sectorStroke": "#49454F",<NL>          "sectorStrokeAlpha": 0.9,<NL>          "hoverFill": "#D0BCFF",<NL>          "hoverFillAlpha": 1.0,<NL>          "hoverStroke": "#49454F",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#1C1B1F",<NL>          "label": "#E6E1E5",<NL>          "hintAlpha": 0.7<NL>        }, |
| 4 | design/skins.json | 114 | "color": { | 18 | <NL>        "color": {<NL>          "dimAlpha": 0.2,<NL>          "sectorFill": "#FFFFFF",<NL>          "sectorFillAlpha": 0.07,<NL>          "sectorScrim": "#000000",<NL>          "sectorScrimAlpha": 0.65,<NL>          "sectorStroke": "#FFFFFF",<NL>          "sectorStrokeAlpha": 0.5,<NL>          "strokeWidth": 1.4,<NL>          "strokeGradient": true,<NL>          "hoverFill": "#000000",<NL>          "hoverFillAlpha": 0.18,<NL>          "hoverStroke": "#FFFFFF",<NL>          "hoverStrokeAlpha": 0.85,<NL>          "hoverLabel": "#FFFFFF",<NL>          "label": "#FFFFFF",<NL>          "hintAlpha": 0.7<NL>        }, |
| 5 | design/skins.json | 110 | tagline | 3 | <NL>      "tagline": "玻璃拟态：白 tint + 黑 scrim 保底，慢而柔",<NL>      "cost": "低-中（scrim 多一层纯几何垫底）",<NL>      "src": "出处：Fluent Acrylic 官方配方（Microsoft Learn《Acrylic material》）—— 半透明 tint 之下必须有保底层，官方并明文「不要在 acrylic 面上放强调色文字」。分层窗口拿不到系统模糊，以纯几何黑 α0.65 垫底近似（spec R2 用户原型挑定 D 档）：白字在最坏纯白壁纸 5.8:1、悬停压暗 7.7:1。真模糊不可用，这仍是「半透明 + 保底」而不是「毛玻璃」。", |
| 6 | design/skins.json | 65 | "color": { | 22 | <NL>        "color": {<NL>          "dimAlpha": 0.55,<NL>          "sectorFill": "#060A14",<NL>          "sectorFillAlpha": 0.88,<NL>          "sectorStroke": "#00E5FF",<NL>          "sectorStrokeAlpha": 0.55,<NL>          "strokeWidth": 1.2,<NL>          "haloPx": 6,<NL>          "haloAlpha": 0.2,<NL>          "haloColor": "#00E5FF",<NL>          "hoverFill": "#00E5FF",<NL>          "hoverFillAlpha": 0.22,<NL>          "hoverStroke": "#00E5FF",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#E0F8FF",<NL>          "label": "#E0F8FF",<NL>          "hintAlpha": 0.75<NL>        }, |
| 7 | design/skins.json | 37 | "color": { | 19 | <NL>        "color": {<NL>          "dimAlpha": 0.28,<NL>          "sectorFill": "#FAF6EF",<NL>          "sectorFillAlpha": 0.97,<NL>          "sectorStroke": "#6B5D4A",<NL>          "sectorStrokeAlpha": 0.22,<NL>          "hoverFill": "#B45309",<NL>          "hoverFillAlpha": 1.0,<NL>          "hoverStroke": "#FFFFFF",<NL>          "hoverStrokeAlpha": 0.9,<NL>          "hoverLabel": "#FFFFFF",<NL>          "label": "#3F3A31",<NL>          "hint": "#3F3A31",<NL>          "hintAlpha": 0.6<NL>        } |
| 8 | design/skins.json | 14 | 新令牌（strokeWidth | 1 | <NL>    "次要表面（deadZone* / badge*）是**派生键**（spec skin-color-redesign R3）：",<NL>    "皮肤不写就按 sectorFill 合成底自动派生；确需身份特例才显式覆盖（如高对比那圈实心白描边）。",<NL>    "新令牌（strokeWidth / halo* / hoverFillAlpha / hoverLabel / childStaggerMs）" |
| 9 | design/tokens.json | 27 | "haloColor": "#000000", | 2 | <NL>    "haloColor": "#000000",<NL>    "sectorScrim": "#000000",<NL>    "sectorScrimAlpha": 0.0,<NL>    "hoverFill": "#0078D4", |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| design/skins.json | "sectorScrimAlpha": 0.65 |
| design/tokens.json | "sectorScrim": "#000000" |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-data.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```