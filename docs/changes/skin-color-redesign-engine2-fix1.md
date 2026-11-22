---
slug: skin-color-redesign-engine2-fix1
status: approved
date: 2026-11-23
tier: L
changed-files: src/WinQuick.App/Design/SystemSkin.cs
---

# SystemSkin 收尾修正：多余闭括号删除 + 注释首行缩进

- 改什么：删除 engine2 第 6 行新内容多写的类闭括号（L68/L69 双 `}`）；补 L61 注释行首缩进。
- 为什么：编译报 CS1022；表达式体不需要方法闭括号，类括号保留在替换块外。
- 影响面：仅 SystemSkin.cs 语法收尾。
- 回滚方式：编译自动快照 docs/changes/skin-color-redesign-engine2-fix1.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/Design/SystemSkin.cs | 68 | } | 2 | } |
| 2 | src/WinQuick.App/Design/SystemSkin.cs | 61 | 悬停字色按强调色自动取黑/白 | 1 | <NL>    /// 悬停字色按强调色自动取黑/白 —— Win11 官方机制（强调色「自动为明暗两模式优化对比度」）。 |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-engine2-fix1.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```