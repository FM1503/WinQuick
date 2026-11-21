# WinQuick

热键唤出的快捷轮盘 —— 把常用程序、文件夹与「启动队列」放到一次按键之下。

（截图占位：轮盘 + 设置面板）

## 功能

- **热键轮盘**：全局热键（默认 Ctrl+Shift+Z）唤出环形菜单，支持子菜单与两层层级导航；
- **启动队列**：一组程序/文件夹按固定间隔依次打开（开机后一键就位）；
- **换肤与形态**：七套皮肤 × 四种盘面形态，悬停高亮色可调；
- **文件右键菜单集成**：资源管理器里右键即可把文件/文件夹加进轮盘；
- **在线更新**：依托 GitHub Releases 静默检查、增量下载（[Velopack](https://velopack.io)），重启由用户决定。

## 下载与安装

到 [Releases](https://github.com/FM1503/WinQuick/releases) 下载最新 `WinQuick-Setup.exe` 安装，或 `WinQuick-Portable.zip` 免安装运行。

> 安装器未做代码签名，首次运行会触发 SmartScreen：点「更多信息」→「仍要运行」。

## 构建

```bash
dotnet publish src/WinQuick.App/WinQuick.App.csproj -c Release -r win-x64 --self-contained -o publish
```

本地打包安装器：

```bash
dotnet tool install -g vpk
vpk pack -u WinQuick -v <版本> -p publish --channel win-x64 --mainExe WinQuick.exe
```

## 开发过程文档

设计决策、实测数据与各里程碑结论见 [docs/](docs/)（分层窗口与 z 序、内存实测、换肤规格、粒子调研等）。

## 许可证

GPL-3.0-or-later，见 [LICENSE](LICENSE)。
