---
slug: skin-panel-readability
tier: M
status: approved
date: 2026-11-21
code-files: src/WinQuick.App/Design/TokenResources.cs, tests/WinQuick.Core.Tests/SkinContrastTests.cs
review: quick
---

# 变更单（M）：面板配色与皮肤的可读性适配

## 根因（逐键审计矩阵，2026-11-21）

面板八键 × 九皮肤态推演：仅 **translucent（玻璃拟态）** 结构性灾难——面板底把 α0.07 白实色化=纯白底，label 白字→全白；FieldBackground（白死区）同病；Outline/HoverSurface 的 fillIsLight 判定用原料 hex（纯白）而合成底实为暗→翻转错向。其余八态（default/纸感/neon/material/contrast/磷光/system 明暗）配对全部可读。
语义错配：轮盘扇区是「原料叠桌面」、面板是「自绘实色表面」——半透明原料必须先**按 α 合成到暗基线**再供面板使用。

## 方案
1. 抽纯函数 `DerivePanelColors(ColorTokens) → PanelPalette`（可单测）：SurfaceSolid/Surface=SectorFill 按 α 合成到 #202020；FieldBackground=合成底 12% 混色一档阶梯（黑底白底均有效）；Accent=HoverFill **原色不透明**（α 是轮盘悬停高亮参数，合成会让低 α 皮肤的面板强调色与底趋同——SkinContrast 抓到后修正）；Outline/HoverSurface 基于**合成底**亮度翻转（修正顺序缺陷）；Danger/Success 按底明暗双套（浅底 #C42B1C/#1B7F3A，暗底原值）。配对阈值：Text↔底 ≥0.3、Accent↔底 ≥0.2、输入框底↔Text ≥0.15。
2. Install 消费该函数。
3. SkinContrastTests 加面板配对 Theory（九态：Text↔底、Accent↔底亮度差≥0.3 断言）+ translucent 回归对照（修复前白底白字用例锁死）。

## 验收标准与测试计划

| 验收标准 | 层级 | 命令 | 预期输出 | 耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 0 错误 | 60s |
| AC2 面板配对单测全绿 | 单元 | dotnet test tests/WinQuick.Core.Tests --filter SkinContrast --nologo | 全绿（原 8+新 10） | 60s |
| AC3 全量不回退 | 单元 | dotnet test tests/WinQuick.Core.Tests --nologo | 339 全绿 | 90s |
| AC4 半透明面板实测 | 用户 | v1.1.5 切 translucent 开面板 | 暗面板白字、输入框/描边层次分明 | 用户侧 |
