---
slug: skin-color-redesign-data-fix1
status: approved
date: 2026-11-23
tier: L
changed-files: design/skins.json
---

# data 批修正：_comment 新令牌行尾逗号

- 改什么：design/skins.json L17 的「新令牌」注释元素补回行尾逗号（data 批重抄时丢失，致 JSON 解析失败）。
- 为什么：SkinContrast 4 测因 skins.json 非法 JSON 全部 TypeInitializationException。
- 影响面：仅注释数组语法；值语义不变。
- 回滚方式：docs/changes/skin-color-redesign-data-fix1.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | design/skins.json | 17 | 新令牌（strokeWidth | 1 |     "新令牌（strokeWidth / halo* / hoverFillAlpha / hoverLabel / childStaggerMs）", |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-data-fix1.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```