---
slug: smart-panel-placement-childarc-asyncload
status: approved
wireframe: approved
review: required
date: 2026-11-21
code-files: src/WinQuick.App/Diagnostics/PanelPlacement.cs, tests/WinQuick.Core.Tests/PanelPlacementTests.cs, src/WinQuick.App/Diagnostics/MonitorLayout.cs, src/WinQuick.App/Hosting/OverlayController.cs, src/WinQuick.App/Interop/ShellIcons.cs, src/WinQuick.App/Radial/RadialRingVisual.ChildArc.cs, preview/spinner-preview.html
---

# 规格：面板智能摆位（零重叠）+ 子环图标异步渐进加载

## 1. 背景与用户决定（2026-11-21）

- ② 设置/皮肤面板弹出会挡住轮盘 → **零重叠优先**：候选锚位中选与轮盘圆重叠为零者，多个则取离光标最近；窄屏全重叠时取重叠最小；
- ③ 安装后首次展开子环卡顿约 1s → 根因（已勘探）：`ShellIcons.Get` 冷启动为每个子项串行 `ExtractFromShell`（磁盘+COM），安装版 iconcache 为空 → **异步渐进加载**，加载期每个扇区图标位显示**环形加载动画占位**；验收 **≤300ms**（spinner 出现到可交互）。

## 2. 方案

### 2.1 面板摆位（②）

新增纯函数 `PanelPlacement.Choose(MonitorInfo monitor, Rect ringRect, int panelW, int panelH, int margin) → (int Left, int Top)`：
- 候选锚位：四角（margin 内缩）、四边中点、左右对侧共 10 个，面板矩形逐一钳制屏内；
- 目标函数：与轮盘圆（ringRect 内切圆）重叠面积；零重叠者中取中心距光标最近；全重叠取重叠最小；并列取稳定序（左上优先，避免抖动）；
- `OverlayController.PositionPanel` 改调此函数（轮盘矩形取自 `_radial` 窗口位置+WindowSize；放置语义其余不变：placedByUser 不动、MaxHeight 钳制保留）；
- 单测覆盖：光标居中/四象限、窄屏（面板+轮盘必重叠）、轮盘贴边、多候选并列稳定性。

### 2.2 子环图标异步加载（③）

- `ShellIcons`：抽 `ExtractFromShell` 到后台线程路径（`GetAsync(path, px)`：缓存命中同步返回，未命中 Task.Run 抽取→写内存/磁盘缓存→回调；UI 线程不再阻塞）；
- 子弧构建（RadialRingVisual.ChildArc）：图标未命中时先渲染 spinner 占位（见 §3），图标就绪后替换；已缓存项零等待直接显示（绝大多数场景）；
- 分段计时埋点：`--childarc-perf-probe`（新增探针，输出 布局/图标抽取/渲染 三段耗时，对照检查含"冷缓存首展 ≤300ms 内出现 spinner"与"图标就绪前 UI 线程无阻塞 >50ms"），实测数据落 docs。

### 2.3 spinner 档位（观感，四选一）

| 档 | 形态 | 参数 | 出处 |
|---|---|---|---|
| ① | 细线渐隐弧 270° | 2px / 900ms / 线性 | Windows 11 ProgressRing 惯例 |
| ② | 粗弧变弧长 | 3px / 1200ms / 标准 easing | Material 3 circular indeterminate |
| ③ | 双色对弧 | 2px / 700ms / 线性 / 主题蓝+白 | 仪表风（社区常见） |
| ④ | 点阵依次点亮 | 8 点 4px / 800ms | 经典 spinner |

档位以 spec 批准后产出的 `preview/spinner-preview.html` 背靠背对比为准，用户选定后实现（XAML Storyboard，WPF 实现，非 CSS）。

## 3. UI 数据表与线框

| # | 元素 | 类型 | 尺寸/字号 | 行为 |
|---|---|---|---|---|
| L1 | spinner 占位 | 自绘（Ellipse×1-8 + Storyboard） | 直径 32px（=图标位），颜色 Wq.Color.Text 派生 | 图标未就绪期间循环播放；就绪即移除 |
| L2 | 图标 | Image | 32px | 缓存命中直接显示（现状） |
| L3 | 扇区标签 | 现状不变 | 现状 | 不受加载影响 |

```
展开中的子环（图标异步加载期）        加载完成
┌───────────────────┐          ┌───────────────────┐
│      ╭─╮          │          │      ┌──┐         │
│      │◠│ ← L1 环动画│          │      │图│ ← L2    │
│      ╰─╯          │          │      └──┘         │
│    子项标签        │          │    子项标签        │
└───────────────────┘          └───────────────────┘
```

## 4. 非功能与边界

- spinner 动画帧成本：Storyboard 仅 RotateTransform/Opacity，无 Effect/位图渲染（分层窗口预算，见 M0/M6）；
- 图标抽取失败：保持现状（占位块），spinner 停止；
- 同一图标并发请求去重（第一个 Task 完成后其余复用）；
- 离线/缓存全命中：行为与现状完全一致（无 spinner 出现）。

## 5. 验收标准与测试计划

| 验收标准 | 层级 | 命令 | 预期输出 | 耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 0 错误 | 60s |
| AC2 摆位单测全绿（≥8 用例） | 单元 | dotnet test tests/WinQuick.Core.Tests --filter PanelPlacement | 全绿 | 60s |
| AC3 首展分段计时（探针） | 集成 | publish 后 probes\WinQuick.Probes.exe --childarc-perf-probe | spinner 出现 ≤300ms；UI 阻塞无 >50ms 段；数据落 docs | 90s |
| AC4 摆位用户实测 | 用户 | （清空 iconcache 后热键唤出→开面板/换肤） | 面板不再压轮盘 | 用户侧 |
| AC5 spinner 观感 | 用户 | （冷缓存首展子环） | 选定档位形态正确、就绪即换 | 用户侧 |

## 6. 交付物

PanelPlacement.cs+单测、ShellIcons 异步化、ChildArc spinner 占位、childarc-perf-probe、spinner-preview.html（档位确认用）、docs 性能数据记录。