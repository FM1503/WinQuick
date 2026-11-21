---
slug: opensource-velopack-updates
status: approved
wireframe: approved
review: required
date: 2026-11-21
code-files: src/WinQuick.App/WinQuick.App.csproj, src/WinQuick.App/Program.cs, src/WinQuick.App/App.xaml, src/WinQuick.App/App.xaml.cs, src/WinQuick.App/Hosting/ResidentHost.cs, src/WinQuick.App/Updates/UpdateChecker.cs, src/WinQuick.App/Radial/SettingsPanel.xaml, src/WinQuick.App/Radial/SettingsPanel.xaml.cs, src/WinQuick.App/Config/ConfigStore.cs, src/WinQuick.Core/Config/AppConfig.cs, tests/WinQuick.Core.Tests/UpdateConfigTests.cs, .github/workflows/release.yml, LICENSE, README.md, .gitignore
---

# 规格：开源到 GitHub（GPL-3.0）+ Velopack 在线更新

## 1. 背景与目标

用户三项决定（2026-11-21）：
- 更新方式：**Velopack 全自动**（后台下载、用户确认后重启更新，依托 GitHub Releases）；
- 开源范围：**公开仓库 + 保留全部 61 个提交历史**；许可证 **GPL-3.0**；
- 更新体验：**启动后静默检查 + 托盘气泡提示 + 设置面板新增「关于与更新」分区**；更新通道**仅正式版**；
- 个人/内部信息清理：**默认菜单换通用示例**（实测 DefaultMenu.cs 本身即通用标签，无需改动；真实标签仅存在于 preview/ 两个原型 HTML 与用户本地 menus.json）。

## 2. 调研结论（关键出处）

| 结论 | 来源 |
|---|---|
| NuGet 包 `velopack`；WPF 需自定义 Main：App.xaml 改 Page + StartupObject，Main 开头 `VelopackApp.Build().Run()` | https://docs.velopack.io/getting-started/csharp |
| 更新三步 API：`CheckForUpdatesAsync()` → `DownloadUpdatesAsync(ver)` → `ApplyUpdatesAndRestart(ver)`；UpdateManager 构造传更新源 URL，不传 channel 则沿用打包时的通道 | https://docs.velopack.io/getting-started/csharp 、https://docs.velopack.io/packaging/channels |
| `vpk pack --packId --packVersion(semver2，不支持 4 段) --packDir --mainExe --channel`；delta 增量包自动生成；产物含 Setup.exe/Portable.zip/full+delta nupkg/releases.{channel}.json | https://docs.velopack.io/packaging/overview |
| GitHub Actions 官方工作流：windows-latest + checkout@v4 + setup-dotnet@v4，`vpk download github` → `vpk pack` → `vpk upload github --publish --tag`；私有仓库需 --token 与 permissions: contents: write | https://docs.velopack.io/distributing/github-actions |
| 开源项目可免费用 GitHub Releases 托管更新；nupkg 与 releases.{channel}.json 须同目录 | https://docs.velopack.io/distributing/overview |
| 通道机制：channel 是纯分发维度（win-x64 等），GitHub prerelease 标记与其无直接关联 | https://docs.velopack.io/packaging/channels |

本地敏感扫描（2026-11-21 实测）：
- 61 个提交全部署名 `FM150333 <2257271313@qq.com>` —— 公开仓库将暴露工号与 QQ 邮箱（风险呈报见 §8）；
- 代码/文档树无 `FM150333`/`22572`/`D:\Work` 命中（obj/ 构建产物有本机路径，已被 .gitignore 忽略不入库）；
- 内部工具名仅存在于 `preview/wheel2-preview.html`、`preview/tree-preview.html`（真实标签数据）与 `docs/M3-status.md` 一处路径示例；
- WinQuick.App.csproj 无 `<Version>`（默认 1.0.0.0，4 段，不满足 Velopack semver2 要求）。

## 3. 方案

### 3.1 仓库与开源清单

| 项 | 内容 |
|---|---|
| LICENSE | GPL-3.0-or-later 全文 |
| README.md | 简介（热键轮盘 + 换肤 + 启动队列）、截图占位、构建命令、安装（Setup.exe / Portable）、GPL-3.0 声明 |
| .gitignore | 追加 `preview/`（原型含真实菜单标签与开发过程数据，不公开；本地保留） |
| docs/M3-status.md | 一处 `C:\MentorGraphics\...` 路径示例改为 `C:\Vendor\...` 通用示例 |
| git 历史 | 保留（邮箱风险处理见 §8 用户决策） |

### 3.2 版本与打包

- csproj 增加 `<Version>1.1.0</Version>`（semver2 三段；AssemblyInfo 版本随 Version 属性走）；
- packId 取 `WinQuick`（可改 `<github用户名>.WinQuick`）；channel 固定 `win-x64`（单通道，仅正式版）；
- mainExe `WinQuick.exe`。

### 3.3 CI 发布链路（.github/workflows/release.yml）

触发：推送 `v*` tag（正式发布动作明确可控；push main 不自动发版）。
步骤（windows-latest）：
1. checkout@v4 → setup-dotnet@v4（net10.0 SDK 版本与当前工程一致）；
2. `dotnet publish src/WinQuick.App/WinQuick.App.csproj -c Release -r win-x64 --self-contained -o publish`；
3. `dotnet tool install -g vpk`；
4. `vpk download github --repoUrl https://github.com/<user>/WinQuick`（取上一版生成 delta）；
5. `vpk pack -u WinQuick -v <tag去掉v前缀> -p publish --channel win-x64 --mainExe WinQuick.exe`；
6. `vpk upload github --repoUrl … --publish --releaseName "WinQuick <版本>" --tag <tag>`。

### 3.4 客户端集成

- 新增 `src/WinQuick.App/Program.cs`：`[STAThread] static void Main()`，首行 `VelopackApp.Build().Run()`，随后手动 `new App()` + InitializeComponent + Run；App.xaml 的 Build Action 由 ApplicationDefinition 改 Page，csproj 设 `<StartupObject>WinQuick.App.Program</StartupObject>`；App.xaml.cs 既有 OnStartup 逻辑不动；
- 新增 `src/WinQuick.App/Updates/UpdateChecker.cs`：
  - 构造 `UpdateManager`（GitHub 源：`new GithubSource("https://github.com/<user>/WinQuick", null, prerelease: false)`，API 签名以编译期校验为准）；
  - `CheckAsync()`：CheckForUpdatesAsync，返回（有更新: bool, 版本, 错误信息）；
  - `DownloadAsync()`：DownloadUpdatesAsync（后台静默下载 delta/full）；
  - `ApplyAndRestart()`：ApplyUpdatesAndRestart；
  - 网络失败一律静默降级（返回错误），不弹窗不打扰——更新是增强不是主功能；
- ResidentHost：启动完成后延迟 15 秒（避开开机高负载，与启动队列同类考量）且 `config.Updates.AutoCheck` 为 true 时静默检查；发现新版→自动下载→完成后托盘气泡「WinQuick 新版本 x.y.z 已就绪，重启后生效」（点击气泡打开设置面板「关于与更新」分区）；
- 下载与安装仅通过用户可见动作触发重启（气泡点击或面板按钮），**绝不自动重启**（常驻工具打断用户工作代价高）。

### 3.5 设置面板新分区「关于与更新」

侧栏（M4 既定侧栏分区形态）末尾新增「关于与更新」；面板宽 404 与现状一致。

UI 数据表：

| # | 元素 | 类型 | 尺寸/字号 | 交互与数据源 |
|---|---|---|---|---|
| U1 | 标题「WinQuick」 | TextBlock | 16px semibold | 静态 |
| U2 | 版本号「版本 1.1.0」 | TextBlock | 12px 次级色 | 读 assembly `<Version>` |
| U3 | 状态行（含色点） | TextBlock | 12px | 文案：检查中…/已是最新/新版本 x 已就绪/检查失败（离线静默不弹窗） |
| U4 | 上次检查时间 | TextBlock | 12px 次级色 | 内存态（不落盘，进程重启即清） |
| U5 | 「检查更新」按钮 | Button | 高 32、最小宽 96 | 点击→CheckAsync→有新版→DownloadAsync→状态行/按钮切换 |
| U6 | 「自动检查」开关 | CheckBox | 行高 32 | 存 config `updates.autoCheck`（默认 true） |
| U7 | 「立即更新并重启」按钮 | Button | 高 32、最小宽 140 | **仅下载完成后出现**；点击→ApplyAndRestart |

ASCII 线框（左：侧栏分区列表；右：分区内容）：

```
┌─设置面板（宽 404）──────────────────────────────┐
│ 侧栏            │ 「关于与更新」内容区            │
│ ────────────    │ ┌───────────────────────────┐ │
│ 外观            │ │ WinQuick                  │ │
│ 行为            │ │ 版本 1.1.0                │ │
│ 启动队列        │ │                           │ │
│ 菜单            │ │ ● 已是最新版本             │ │
│ 系统位置        │ │ 上次检查 今天 09:41        │ │
│ ──────────      │ │                           │ │
│ ◉ 关于与更新    │ │ [检查更新]  自动检查 ☑     │ │
│                 │ │                           │ │
│                 │ │ （有新版且已下载时：）      │ │
│                 │ │ ● 新版本 1.2.0 已就绪      │ │
│                 │ │ [立即更新并重启]           │ │
│                 │ └───────────────────────────┘ │
└─────────────────────────────────────────────────┘
```

配色/圆角/间距全部沿用现有设置面板令牌（design/tokens.json），不引入新视觉参数。

### 3.6 自启路径自愈（迁移必配）

用户从 `D:\Work\WinQuick\publish\WinQuick.exe` 迁移到 Velopack 安装目录（%LOCALAPPDATA%\WinQuick\）后，注册表 Run 项仍指向旧路径。ResidentHost.Start 在既有自启种子逻辑旁增加：若 Run 项存在且其路径 ≠ `Environment.ProcessPath` → 用当前路径改写（保持用户「已启用」的意图，仅纠正位置）。

### 3.7 配置新增段

AppConfig 新增 `UpdateConfig`（JsonPropertyName "updates"，默认实例 AutoCheck=true）；ConfigStore.RenderConfig 模板补该段渲染；解析端 case-insensitive 自动兼容旧配置（缺段=默认）。

## 4. 非功能要求

- 静默检查不阻塞启动：后台 Task，超时 15s 取消；网络不可达静默失败；
- 下载占用低优先级（Velopack 默认行为），不与启动队列抢 IO；
- 无代码签名：SmartScreen 对 Setup.exe 首次运行可能告警（开源免费项目常态，README 注明「更多信息→仍要运行」操作）。

## 5. 边界情况

- 用户离线/代理环境：检查失败 → 状态行「检查失败」，无弹窗；
- GitHub 限流：同上静默；
- tag 未推但 Releases 手动传过产物：以 releases.win-x64.json 为准（vpk upload 维护）；
- 从旧 publish 目录迁移：Run 项路径自愈（§3.6）；旧目录 exe 可继续手动跑（无冲突，单实例互斥锁兜底）。

## 6. 验收标准与测试计划

| 验收标准 | 测试层级 | 命令 | 预期输出 | 预计耗时 |
|---|---|---|---|---|
| AC1 编译 0 error | 静态 | `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` | 已成功生成 0 错误 | 30s |
| AC2 UpdateConfig 读写往返（默认/显式 false/缺段兼容） | 单元 | `dotnet test tests/WinQuick.Core.Tests --filter UpdateConfig` | 全绿，含缺段=默认 true 用例 | 30s |
| AC3 RenderConfig 含 updates 段且 JSON 可解析 | 集成（探针） | 编译后运行 `publish\probes\WinQuick.Probes.exe --config-render-probe`（新增，输出渲染 JSON 并解析回） | 解析成功且 autoCheck 字段往返一致 | 20s |
| AC4 vpk 本地打包演练成功 | 集成 | `dotnet publish … -o publish && vpk pack -u WinQuick -v 1.1.0 -p publish --channel win-x64 --mainExe WinQuick.exe`（不 upload） | 生成 WinQuick-Setup.exe 与 releases.win-x64.json | 3min |
| AC5 设置面板「关于与更新」分区可见、检查按钮状态流转 | 用户实测 | （交付后用户操作） | 分区显示版本/状态/开关 | 用户侧 |
| AC6 完整更新闭环（发 1.1.0→装→发 1.2.0→面板检查→下载→重启升级） | 用户实测 | （GitHub 建仓后，用户配合） | 重启后「关于」显示 1.2.0 | 用户侧 |

## 7. 交付物

代码：§3.4/3.5/3.6/3.7 全部文件；CI：.github/workflows/release.yml；文档：LICENSE、README.md、.gitignore 增项、docs/M3-status.md 路径脱敏；测试：UpdateConfigTests.cs、config-render-probe。

## 8. 风险与用户决策点

1. **git 历史作者信息**（工号 FM150333 + QQ 邮箱将随 61 个提交公开）：三选一——接受公开 / `git filter-repo` 重写作者邮箱为 GitHub noreply（所有 commit hash 改变，视为「保留内容的历史改写」）/ 放弃公开转私有；
2. **preview/ 目录**：建议加入 .gitignore 不公开（含真实菜单标签），如希望公开需先换假数据；
3. **packId**：默认 `WinQuick`，如需更强的全局唯一性可用 `<github用户名>.WinQuick`；
4. **SmartScreen**：无签名安装器首次下载会告警，README 引导。

## 9. 执行阶段划分（任务书依据）

- 阶段一（客户端）：Program.cs/App.xaml/csproj/UpdateChecker/ResidentHost/SettingsPanel/ConfigStore/AppConfig + 单测 + config-render-probe；
- 阶段二（仓库）：LICENSE/README/.gitignore/docs 脱敏；
- 阶段三（CI）：release.yml（本地 `vpk pack` 演练替代 CI 实测，真实 CI 在用户建仓推送后首跑验证）。
