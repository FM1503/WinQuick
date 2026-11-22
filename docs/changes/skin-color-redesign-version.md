---
slug: skin-color-redesign-version
status: approved
date: 2026-11-23
tier: S
changed-files: src/WinQuick.App/WinQuick.App.csproj
---

# v1.2.0 版本号（行号现场核实：`<Version>` 在 L12）

- 改什么：csproj L12 `<Version>1.1.5</Version>` → `<Version>1.2.0</Version>`。
- 为什么：皮肤配色全量重设计定稿发版（spec skin-color-redesign，用户验收通过）。
- 影响面：Velopack 更新索引与「关于」版本；tag v1.2.0 触发 CI。
- 回滚方式：docs/changes/skin-color-redesign-version.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/WinQuick.App.csproj | 12 | 1.1.5 | 1 | <NL>    <Version>1.2.0</Version> |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| src/WinQuick.App/WinQuick.App.csproj | <Version>1.2.0</Version> |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-version.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo
```