# WinQuick

Windows 10/11 的快捷访问轮盘：按热键唤出全屏遮罩 + 圆形轮盘，悬停展开子菜单，点击启动。
菜单可以在轮盘上直接增删改：进设置后环里会多出一个「＋」，点它加一项；扇区上点右键改名或移除。

技术栈 **C# + WPF (.NET 10)**，运行期无浏览器引擎依赖，常驻内存约 **75 MB**
（任务管理器「专用工作集」口径，2026-09-14 实测 74.5 MB；<a>**未达 60 MB 目标**</a>，
原因与分阶段数据见 [M0 实测结论](docs/M0-findings.md#3-m03-内存实测--未达标)）。

---

## 当前状态

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 风险清零：环境实测、Win+W 可用性、内存实测、视觉验证机制 | 完成 |
| M1 | 热键三级降级、每屏遮罩、焦点保存还原、托盘、单实例、开机自启 | 完成 |
| M2 | 视觉规格单一真源、真实图标、入场/退场动画 | 完成 |
| M3 | 动作执行、文字折行、悬停形变、环 2 子菜单与悬停状态机、UWP 支持 | 完成 |
| M4 | 配置系统 + 设置界面 + 就地编辑菜单（环里的「＋」与右键扇区） | 完成（打包未做） |

各阶段的实测数据与结论见 [`docs/`](docs/)：

- [M0 实测结论](docs/M0-findings.md) —— 环境、Win+W、内存模型、视觉验证方法
- [M1 状态](docs/M1-status.md) —— 热键与遮罩，含两个被验证机制抓出的 bug
- [M2 状态](docs/M2-status.md) —— 视觉规格与图标
- [M3 进展](docs/M3-status.md) —— 动作执行、文字处理、环 2 子菜单、UWP 支持
- [M4 进展](docs/M4-status.md) —— 配置系统：两份配置的分工与热重载
- [窗口层级与焦点架构](docs/z-order-architecture.md) —— 多个置顶窗口的层级栈、根因排查与验证手段

---

## 运行

```bash
dotnet build -c Release
src/WinQuick.App/bin/Release/net10.0-windows/WinQuick.exe
```

启动后托盘出现图标，按 **Ctrl+Win+W** 唤出轮盘。

> **热键不是 Win+W。** 实测 Win+W 在 Windows 11 上被 shell 预留，`RegisterHotKey`
> 必然返回 1409。Ctrl+Win+W 可以正常注册。详见 [M0 文档](docs/M0-findings.md)。

## 开发提示

**重新构建前必须退出正在运行的 WinQuick**，否则 `dotnet build` 会因为
`WinQuick.Core.dll` 被占用而失败（程序集被已加载的进程锁定）。

```powershell
Get-Process WinQuick -ErrorAction SilentlyContinue | Stop-Process -Force
```

托盘程序在跑是常态（这正是它的用途），所以这条会反复遇到 ——
先杀进程再编译是最省事的做法。

另一个相关的小坑：用 `Start-Process` 启动的进程会以 `explorer.exe` 作为父进程
（它走的是 ShellExecute 路径），排查"进程是谁启动的"时不要被这一点误导。

## 诊断探针

这些命令既是排查工具，也是本项目的验证手段 —— 界面好不好看只能靠人眼，
但几何、配色、透明、内存、点击链路都是可以程序化核对的。

```
WinQuick.exe                          托盘常驻
WinQuick.exe --env                    DPI-aware 环境与显示器报告
WinQuick.exe --hook-probe --inject    热键注册能力 + 键盘钩子截获能力
WinQuick.exe --hook-probe --interactive=15   人工按键复核
WinQuick.exe --mem-probe --hold=20    分阶段内存回归（--hold 保持存活供任务管理器核对）
WinQuick.exe --overlay-probe          覆盖层：窗口真实可见性、位置钳制、焦点还原、点击检测
WinQuick.exe --overlay-probe --hold=30        显示轮盘供肉眼查看
WinQuick.exe --action-probe           动作执行链路
WinQuick.exe --action-probe --uwp     同上，且实际启动一个 UWP 应用并自动关闭
WinQuick.exe --apps-probe             枚举 shell:AppsFolder，核对 AUMID 与图标
WinQuick.exe --render-preview         离屏渲染成 PNG（--state=hover / --stress-labels）
tools/env-report.ps1                  环境报告（PowerShell 版）
tools/analyze-render.ps1 -Path <png>  对渲染结果做极坐标数值验证
```

```bash
dotnet test          # 73 条单测：几何、命中测试、视觉规格约束、贝塞尔求值
```

---

## 配置与自定义

配置在 `%APPDATA%\WinQuick\`。可以**手改 JSON**，也可以**从托盘菜单 →「设置…」**打开图形界面
（热键录制与实时测试、遮罩深浅、悬停延迟、开机自启）。
两种方式都**保存即生效，无需重启**；写错也不会导致打不开 —— 会保留上一份有效配置并在托盘提示。

### 直接在轮盘上改菜单

不必手写 JSON —— 打开设置（左上角「设置」或托盘菜单 →「设置…」）后，**轮盘上会多出一个「＋」扇区**：

| 操作 | 效果 |
|---|---|
| **左键外圈末尾的「＋」** | 先问一句：**添加程序…** 还是 **新建分组…**（带子菜单的项，像「开发工具」） |
| **添加程序…** | 弹出文件浏览器，选中的程序加进**那一级**（子环里的「＋」加进该子菜单） |
| （子环的「＋」） | 同样先弹面板，只是只有「添加程序…」—— 分组只能建在最外层 |
| **新建分组…** | 起个名字 → 选一个程序作为它的第一项；之后把鼠标移到分组上，在它的子环里点「＋」继续加 |
| **右键任意扇区** | 就地弹出「重命名 / 移除」 |
| 设置面板 →「打开 menus.json」 | 一次改很多项，或做批量替换 |

分组里至少要有一项 —— 空分组会变成一个点不动、也不展开的扇区，所以不让它存在。

改动立刻生效，覆盖层**不会关闭、也不会重开**：加完就能看见新扇区长出来。
名称自动取自程序的版本信息（跟随系统语言），不满意可以直接右键改。

「＋」与右键编辑只在设置态出现 —— 日常唤出时轮盘里只有你自己的项，没有编辑入口。

| 文件 | 内容 |
|---|---|
| `config.json` | 热键、遮罩透明度、悬停延迟、是否动画、自启 |
| `menus.json` | 菜单项、图标、动作、子菜单、角度 |
| `design/tokens.json` | 设计基线（配色/半径/时长/曲线），随程序嵌入，不是给用户改的 |

**同一个值不能有两个来源。** `config.json` 里所有可选项默认是 `null`，
含义是「沿用设计基线」。两处都能改同一个值时会静默分叉，
而这个项目已经为此付出过一次代价（见 M2 文档）。

手写 JSON 时，往菜单里加一项是这样：

```json
{
  "label": "你的项目",
  "icon": "C:\Tools\myapp.exe",
  "action": { "type": "launch", "target": "C:\Tools\myapp.exe", "args": "--flag" }
}
```

`angle` 可省略（省略则均匀分布），指定时必须严格递增。
UWP / 商店应用没有可执行文件，`icon` 与 `action.target` 写
`shell:AppsFolder\<AppUserModelId>`（例如 `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`），
`action.type` 用 `shell`。

## 打包

```powershell
tools/publish.ps1                     # 自包含单文件 -> publish/WinQuick.exe（约 125 MB）
tools/publish.ps1 -FrameworkDependent # 框架依赖，需目标机有 .NET 桌面运行时 10
```

**不要开 `EnableCompressionInSingleFile`。** 实测四种方案的常驻内存（静置 12 秒后取
任务管理器「专用工作集」口径）：

| 方案 | 磁盘 | 内存（当时实测） |
|---|---|---|
| 自包含单文件 **+ 压缩** | 59 MB | **128.5 MB** |
| 自包含单文件 不压缩 | 125 MB | 66.4 MB |
| 自包含散装目录 | 133 MB | 65.4 MB |
| 框架依赖 | ~1 MB | 67.2 MB |

压缩后的程序集**无法被内存映射**，运行时必须解压到私有内存，于是私有工作集大涨。
它花 62 MB 常驻内存换 66 MB 磁盘 —— 对常驻程序是完全错误的取舍，
而本项目的内存目标就在 60 MB 量级，开压缩直接翻倍。

> 上表是**同一次测量内**的横向比较，结论（不压缩明显更省）不受时间影响；
> 但绝对值已经漂移：2026-09-14 复测「自包含单文件 不压缩」是 **74.5 MB**，比表里的
> 66.4 MB 高了约 8 MB。这 8 MB 不是某一处改动造成的 ——
> 同一天的 A/B（同一台机器、同一命令，只差一次提交）显示前后都是 74.5 MB。
> 要省内存，可动的杠杆只有窗口尺寸（见下）与固定底，需要时按下面第 3 条逐段定位。

## 设计约束（不是审美偏好，是实测反推的）

**窗口边长 ≤ 740 DIP。** 实测内存 ≈ 47 MB 固定底 + 24 字节/DIP²，
要落进 60 MB 目标就不能再大。这条由单测锁住，改之前请先重跑 `--mem-probe`。

**遮罩层用原生 Win32 窗口，不用 WPF。** WPF 版遮罩窗口哪怕只是隐藏着也要多占 61 MB
（它为一个纯色窗口也分配了完整渲染目标），原生窗口只占 0.1 MB。
而且必须自注册窗口类 —— 复用系统预定义的 `STATIC` 类会得到灰色画刷（不是黑纱），
并且 `CreateWindowEx` 不会使用我们提供的窗口过程，点击关闭会完全失效。

**不用 WPF 的 Effect（`DropShadowEffect` / `BlurEffect`）。** 每个 Effect 都要占一张
中间表面，叠多了整个窗口会退化到 WARP 软件渲染 —— 那是 WPF 界面掉帧最常见的原因。
层次感用描边与明暗对比表达。

**视觉规格只有一个真源。** 配色、半径、时长、缓动曲线全部在
[`design/tokens.json`](design/tokens.json)，应用以嵌入资源在运行时读取。
刻意不用"代码生成常量"—— 那会引入第二种真源，改了 JSON 忘了重新生成就悄悄分叉。

**动效参数有出处。** 入场 167ms + `cubic-bezier(0,0,0,1)` 来自微软官方 Motion 参数表；
悬停展开 200ms / 收起 350ms 来自 Cockburn IJHCS 2024 的实测（200ms 太快会误展开
且 70% 受试者偏好更慢的一侧）；悬停高亮 ≤100ms 因为它是输入反馈而非装饰。

---

## 已知限制

1. **前台是管理员权限窗口时热键不响应** —— UIPI 隔离，设计使然。
   PowerToys Run / Flow Launcher / Wox 都有同样的限制。
2. **独占全屏游戏下覆盖层不可见** —— DWM 不参与合成，无解，需游戏切无边框窗口。
3. **UWP / 商店应用已支持**（枚举 `shell:AppsFolder` 取 AppUserModelId，
   图标与启动都走 shell 路径，已端到端验证）。
4. **多显示器与混合 DPI 未经验证** —— 开发机是单屏 3072×1920 @ 175%。
   定位代码按物理像素 + 每屏钳制写，但没有在真实多屏环境跑过。
5. **未做真 3D** —— WPF 的 3D 管线无自定义着色器、无后处理，2008 年后冻结。
   立体层次感用 2D 手段表达。
6. 反作弊游戏可能屏蔽键盘钩子 —— 不做任何绕过。

---

## 工程结构

```
design/tokens.json        视觉规格单一真源
src/WinQuick.Core/        纯逻辑，不依赖 Win32，可单测
    Geometry/             极坐标、扇区分配、命中测试
    Design/               视觉规格模型与校验
    Motion/               三次贝塞尔求值
    Config/               菜单与动作模型
    Hotkey/               热键模型与解析
src/WinQuick.App/         托盘主进程
    Interop/              原生窗口、键盘钩子、图标提取、焦点管理
    Hosting/              热键管理、覆盖层编排、单实例、会话监听、常驻装配
    Radial/               轮盘窗口、视觉构造、标签、悬停形变
    Actions/              动作执行
    Diagnostics/          显示器拓扑、探针输出
    M0/                   各诊断探针（保留为长期回归工具）
tests/                    单元测试
tools/                    环境报告与渲染数值分析脚本
```
