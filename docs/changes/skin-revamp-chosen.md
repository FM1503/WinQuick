---
slug: skin-revamp-chosen
tier: M
status: approved
date: 2026-11-21
code-files: design/skins.json, src/WinQuick.Core/Design/DesignTokens.cs, src/WinQuick.App/Radial/RadialRingVisual.cs, src/WinQuick.App/Radial/SectorGeometry.cs, src/WinQuick.App/Radial/RingForm.cs
review: quick
---

# 变更单（M）：按原型挑定档替换三皮肤两形态

用户 2026-11-21 于 preview/skin-revamp-preview.html 挑定：浅色→L3 纸感米白、半透明→T1 玻璃拟态、复古像素→R3 磷光终端、圆角楔形→W3 大圆角密环、花瓣→P3 柔化现状。

## 期望行为
1. 三皮肤配色按档替换（原型 pal 参数为单一真源）；
2. T1 增「整窗垂直渐变描边」渲染能力（color.strokeGradient 新令牌，MappingMode=Absolute 防扇区各自取段；原型中的 glow 因分层窗口无 Effect 的既有约束降级为既有 hoverStroke 增强线）；
3. W3：圆角瓶颈实测在角度封顶（sweep/4）而非半径值——封顶提至 sweep*0.45、半径 14→22 协同；
4. P3：花瓣外凸系数 1.18→1.10。

## 验收标准与测试计划

| 验收标准 | 层级 | 命令 | 预期输出 | 耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 0 错误 | 60s |
| AC2 新配色过校验+全量不回退 | 单元 | dotnet test tests/WinQuick.Core.Tests --nologo | 全绿（SkinContrast 含三套新配色） | 90s |
| AC3 换肤探针仍全过 | 集成 | 发布后 probes\WinQuick.Probes.exe --skin-probe | 全部 PASS | 60s |
| AC4 观感实测 | 用户 | （v1.1.4 升级后切三皮肤两形态） | 与原型一致 | 用户侧 |
