---
slug: childarc-asyncload
spec: docs/specs/smart-panel-placement-childarc-asyncload.md
date: 2026-11-21
---

# 任务书 Q：子环图标异步渐进加载（spec §2.2/§2.3，spinner 档②）

## 目标文件

src/WinQuick.App/Interop/ShellIcons.cs（GetOrBeginGet）、src/WinQuick.App/Radial/SectorSpinner.cs（新）、src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs（AddChildIcon 三分支）、src/WinQuick.Probes/M0/ChildArcPerfProbe.cs（新）、src/WinQuick.Probes/ProbesHost.cs（注册）、tests/WinQuick.Core.Tests/（无新单测——异步回调属 UI 集成，由探针验收）。

## 期望行为

1. 缓存命中（含负缓存）完全同步现状；未命中项不阻塞 UI：后台抽取→UI 线程回调替换；
2. 同一路径并发请求去重（共享同一进行中 Task）；
3. 子弧图标三分支：命中→直接显示；无路径→首字占位（现状）；未就绪→环形 spinner（档②：3px 粗弧 1200ms，背景环 15% 透明度）就绪即换、失败换首字；
4. spinner 登记进 _childExtraVisuals 随收起自动清理；回调到达时元素已离树则丢弃；
5. --childarc-perf-probe：清缓存→展开子弧→分段计时（同步段 ≤300ms 出 spinner；图标全部就绪；热缓存对照）。

## 验收标准与测试计划

| 验收标准 | 层级 | 命令 | 预期输出 | 预计耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 0 错误 | 60s |
| AC2 全量单测不回退 | 单元 | dotnet test tests/WinQuick.Core.Tests --nologo | 321 全绿 | 60s |
| AC3 首展分段计时 | 集成 | 发布后 probes\WinQuick.Probes.exe --childarc-perf-probe | 冷缓存同步段 ≤300ms；图标全就绪；含热缓存对照；结论可失败 | 120s |
| AC4 spinner 观感用户实测 | 用户 | （清缓存后首展子环） | 档②形态正确、就绪即换 | 用户侧 |