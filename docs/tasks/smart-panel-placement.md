
---
slug: smart-panel-placement
spec: docs/specs/smart-panel-placement-childarc-asyncload.md
date: 2026-11-21
---

# 任务书 P：面板智能摆位（spec §2.1，零重叠优先）

## 目标文件

src/WinQuick.App/Diagnostics/PanelPlacement.cs（新）、tests/WinQuick.Core.Tests/PanelPlacementTests.cs（新）、src/WinQuick.App/Hosting/OverlayController.cs（PositionPanel 接线）。

## 期望行为

1. 纯函数 PanelPlacement.Choose(Input)→Result：10 候选锚位（四角/上下边中点/左右中点/左右对侧垂直跟随光标），面板矩形钳屏内，与轮盘内切圆重叠面积（32×32 网格采样）为目标函数：零重叠取离光标最近、全重叠取最小、并列取稳定序；
2. PositionPanel 以 `_radial.CurrentPhysicalBounds` 为轮盘矩形改调 Choose（轮盘矩形拿不到时退化为点圆=按距离选）；placedByUser 不动、MaxHeight 钳制保留；
3. 单测 ≥8 例覆盖：光标左半/中央/贴边轮盘、窄屏必重叠、面板高于屏、距离决胜、并列稳定、退化输入不抛异常。

## 验收标准与测试计划

| 验收标准 | 层级 | 命令 | 预期输出 | 预计耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 0 错误 | 60s |
| AC2 摆位单测 ≥8 全绿 | 单元 | dotnet test tests/WinQuick.Core.Tests --filter PanelPlacement | 全绿 | 60s |
| AC3 摆位用户实测 | 用户 | （唤出轮盘→开设置/皮肤面板，光标在各象限试） | 面板不再压轮盘 | 用户侧 |
