---
slug: skin-formalization
status: draft
code-files: src/WinQuick.App/Radial/SettingsPanel.xaml, src/WinQuick.App/Radial/SettingsPanel.xaml.cs, src/WinQuick.App/Radial/SkinPanel.xaml, src/WinQuick.App/Radial/SkinPanel.xaml.cs, src/WinQuick.App/Design/TokenResources.cs, src/WinQuick.Core/Design/DesignTokens.cs, design/tokens.json, design/skins.json, src/WinQuick.App/Design/TokenStore.cs, src/WinQuick.App/Design/SystemThemeWatcher.cs, tests/WinQuick.Core.Tests/SkinContrastTests.cs
---

# 规格：皮肤功能正式化（评审 D2/D4/D8/D1 + 跟随系统）

依据：docs/specs/skin-maturity-review.md（用户 2026-11-21 批准路线：P0 全项 + D8 + P1 跟随系统优先；七套全留+动效按 M3 规矩收紧）。

## 方案

### 1. D8 材质色全量令牌化（浅色显示问题）

硬编码清单与去向（实测 23 处中，HSV 调色盘的色相环渐变/饱和度轨道为**功能色**，保留并加注释声明）：
- 白系描边（SkinPanel.cs:234 #FFFFFF/0.18、SettingsPanel.cs:524/542 #FFFFFF/0.15、xaml SvDot 等）→ 新语义令牌 `Wq.Color.Outline`（由 SectorStroke 派生）；
- 深色底块（SettingsPanel.cs:541 #2A2F3A）→ `Wq.Color.SurfaceSolid` 派生；
- 白系悬停底（cs:968 #FFFFFF/0.045、1073 #FFFFFF/0.05、xaml:128 #08FFFFFF、132 #10FFFFFF）→ 新令牌 `Wq.Color.HoverSurface`（深肤=白 4-6% α、浅肤=黑 4-6% α，由皮肤明暗派生——Install 时按 SectorFill 亮度自动取黑/白）。
机制：TokenResources.Install 增派 Outline/HoverSurface 两键（值随皮肤自动翻转，浅色皮肤下自动黑系——「明暗互换」零人工）。

### 2. D2 面板跟随换肤

SettingsPanel/SkinPanel.xaml 的 StaticResource 引用（实测 78 处）改为 DynamicResource（画刷/样式键），使 Install(force) 重写资源后面板即时变色；换肤时若面板在场无需重建（机制见 TokenResources.cs:30 注释）。样式模板内 Setter 逐项排查。

### 3. D4 动效合规收紧（M3 规矩规则化）

- DesignTokens.Validate 增规则：`entranceScaleEasing` 贝塞尔控制点 y>1（过冲）**仅允许出现在 transform 类令牌**（现状天然满足，规则防未来越线）；retro 的 steps(4) 为风格声明，在 skins.json 的 src 字段补注「复古像素风格化动效，M3 规矩的风格例外」；
- material 的过冲幅度（bezier y2=1.21）对齐 M3 expressive 官方档（damping≈0.8 的可见过冲），维持不调——已合规。

### 4. D1 还债

design/tokens.json 删除（或注释标注「预留：M9 未采用」）perspective 令牌对；DesignTokens.cs 对应属性标注 Obsolete 或移除（以现文件实际引用为零为准）。

### 5. P1 跟随系统（第八套动态皮肤）

新增 `SystemThemeWatcher`（Design/）：注册表读取 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` 的 `AppsUseLightTheme` 与 `DWM` 的 `AccentColor`（ColorizationColor 回退），经 WM_SETTINGCHANGE 触发刷新（常驻窗口既有消息泵）；输出「明暗+强调色」到 TokenStore 合成一套动态 overrides（映射 sectorFill/label/hint/stroke/accent 等，参照 light 皮肤的键集合）——作为 skins 列表第八项「跟随系统」（无 JSON 文件，运行时合成）。托盘图标不随肤（保持现状，注明）。

### 6. 校验与测试

- DesignTokens.Validate 补对比度配对：label↔sectorFill、hint↔sectorFill、hoverLabel↔hoverFill（已有）、badge 为纯色块无文字（已核实），无文字配对可查；亮度判据沿用 Rec.709 阈值 0.35；
- 新单测 SkinContrastTests：七套皮肤×明暗派生（HoverSurface 翻转逻辑）全组合过校验（8 套含跟随系统的明/暗两态=9 组）；
- 探针 `--skin-probe`：换肤后面板资源键实时变化（读 Application.Resources 两键前后对比）+ 皮肤×面板无异常。

## 验收标准与测试计划

| 验收标准 | 层级 | 命令 | 预期输出 | 耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo | 0 错误 | 60s |
| AC2 皮肤对比度单测全绿 | 单元 | dotnet test tests/WinQuick.Core.Tests --filter SkinContrast | 全绿（≥9 组） | 60s |
| AC3 全量基线不回退 | 单元 | dotnet test tests/WinQuick.Core.Tests --nologo | 321+新增全绿 | 90s |
| AC4 换肤探针 | 集成 | probes\WinQuick.Probes.exe --skin-probe | 面板资源键随肤翻转、无异常、结论可失败 | 60s |
| AC5 浅色皮肤实测 | 用户 | 切浅色皮肤逐面板查看 | 无深色块/白字白边错乱 | 用户侧 |
| AC6 面板跟随实测 | 用户 | 换肤时设置/皮肤面板在场 | 面板即时变色不重建 | 用户侧 |
| AC7 跟随系统实测 | 用户 | 切「跟随系统」+系统切明暗 | 轮盘/面板随系统翻转 | 用户侧 |

## 边界

- HSV 调色盘功能色（色相环/饱和度轨）不令牌化，注释声明；
- 跟随系统的托盘图标/日志不随肤；
- 高对比系统主题不自动映射（contrast 皮肤手动选）。
