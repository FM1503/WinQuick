---
slug: skin-color-redesign-polish3
status: approved
date: 2026-11-23
tier: L
changed-files: src/WinQuick.App/Design/TokenResources.cs
---

# polish 补漏：TokenResources 两处漏网 column-0（锚点现场核实修正版）

- 改什么：L49「强调色兜底」注释行与 L26 面板派生键 summary 注释行补回缩进（两处都是 polish 单新内容首行被 compile-change trim 掉前导空格所致）。
- 为什么：增量快审唯一未决 P2。
- 影响面：纯格式。
- 回滚方式：docs/changes/skin-color-redesign-polish3.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/Design/TokenResources.cs | 49 | 强调色兜底 | 1 | <NL>        // 强调色兜底（spec R1 连带修正）：D 档玻璃的 HoverFill 是压暗黑（悬停语义）， |
| 2 | src/WinQuick.App/Design/TokenResources.cs | 26 | 面板派生键的结果 | 1 | <NL>    /// <summary>面板派生键的结果（纯数据，供 Install 与单测共用）。</summary> |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| src/WinQuick.App/Design/TokenResources.cs |         // 强调色兜底（spec R1 连带修正） |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-polish3.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```