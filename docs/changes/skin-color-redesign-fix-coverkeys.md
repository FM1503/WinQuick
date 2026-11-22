---
slug: skin-color-redesign-fix-coverkeys
status: approved
date: 2026-11-23
tier: L
changed-files: src/WinQuick.Core/Design/SecondaryColors.cs, design/skins.json, tests/WinQuick.Core.Tests/SkinCatalogTests.cs
---

# 回填覆盖判断的大小写缺陷修复 + 现场外观清理（行号按 2026-11-23 现场核实版）

- 改什么：SecondaryColors L43 的 covered 判断改 ToLowerInvariant（大小写错配导致皮肤显式的 deadZoneStrokeAlpha 被派生值盖掉，contrast/translucent 两条矩阵行因此红）；skins.json L131 补回缩进；CatalogTests 删 [Fact] 后空行。
- 为什么：见 spec R3/R6 —— 「皮肤可显式覆盖」通道必须真能覆盖。
- 影响面：显式覆盖次要面键的两套皮肤的描边 α 恢复语义；其余皮肤 covered 为空、行为不变。
- 回滚方式：docs/changes/skin-color-redesign-fix-coverkeys.rollback.json。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.Core/Design/SecondaryColors.cs | 43 | covered.Contains(key) | 1 | <NL>            if (!covered.Contains(key.ToLowerInvariant())) color[key] = value; |
| 2 | design/skins.json | 131 | deadZoneStrokeAlpha | 1 | <NL>          "deadZoneStrokeAlpha": 0.55 |
| 3 | tests/WinQuick.Core.Tests/SkinCatalogTests.cs | 133 | [Fact] | 2 |     [Fact] |

## expect_after

| 文件 | 包含子串 |
| --- | --- |
| src/WinQuick.Core/Design/SecondaryColors.cs | key.ToLowerInvariant() |

## 验证

```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-fix-coverkeys.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests -c Release --nologo
```