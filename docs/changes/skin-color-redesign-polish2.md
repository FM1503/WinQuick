---
slug: skin-color-redesign-polish2
status: approved
date: 2026-11-23
tier: L
changed-files: docs/specs/skin-color-redesign.md
---

# polish 收尾：原型口径注记（唯一遗留；xaml 空行判定无害放弃）

- 改什么：spec §3.6 selfCheck 行补口径注记（reviewer P2-3）。
- 为什么：polish 单该条因行号漂移未落；xaml 保护空行无锚可指且无害，经决策放弃清理。
- 影响面：仅文档。
- 回滚方式：docs/changes/skin-color-redesign-polish2.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | docs/specs/skin-color-redesign.md | 143 | 页内 selfCheck() | 1 | <NL>- 页内 selfCheck()（onerror + 关键配对计算复核），`node preview/_verify.mjs` 可跑纯逻辑自检。<NL>  注（reviewer P2-3 勘误）：原型 selfCheck 的 W4/W5 按面板合成底口径，比单测口径乐观；单测按实际渲染分支（scrim 保底盘死区、角标叠扇区底）判定 —— 终判以单测与实机为准，两者当时皆过。 |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| docs/specs/skin-color-redesign.md | 终判以单测与实机为准 |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-polish2.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests -c Release --nologo
```