# 执行书：opensource-veloppack-repo-ci（按步执行，old 不匹配即停报）

## B1 新建 LICENSE
执行：`curl -fsSL https://www.gnu.org/licenses/gpl-3.0.txt -o LICENSE`（工作区根目录）
校验：`head -2 LICENSE` 输出须含「GNU GENERAL PUBLIC LICENSE」与「Version 3」；不满足即停报。

## B2 新建 README.md（全文如下）
````markdown
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

## 许可证

GPL-3.0-or-later，见 [LICENSE](LICENSE)。
````

## B3 .gitignore 追加（文件末尾）
先 Read .gitignore 最后 10 行，以实际末行为 old 锚点，在其后追加（保留原末行原样）：
```


# Velopack 本地打包产物（vpk pack 输出）
Releases/

# 原型目录含真实菜单标签等内部数据，不随仓库公开（本地保留）
preview/
```

## B4 docs/M3-status.md 脱敏
先 Read 该文件 370-376 行，定位含 `C:\MentorGraphics` 的完整行作为 old；new 为同一行但把 `C:\MentorGraphics` 替换为 `C:\Vendor`（其余字符含省略号原样）。仅此一处。

## B5 新建 .github/workflows/release.yml（全文如下）
```yaml
name: Release

# 正式发布动作明确可控：推 v* tag 才发版（push main 不发）。
on:
  push:
    tags: ['v*']

permissions:
  contents: write

jobs:
  release:
    runs-on: windows-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: 安装 .NET SDK
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x

      - name: 取版本号（tag 去 v 前缀）
        id: ver
        shell: bash
        run: echo "version=${GITHUB_REF_NAME#v}" >> $GITHUB_OUTPUT

      - name: Publish
        run: dotnet publish src/WinQuick.App/WinQuick.App.csproj -c Release -r win-x64 --self-contained -o publish

      - name: vpk 打包并上传 GitHub Releases
        # download 取上一版产物以生成 delta 增量包；upload 自动创建 Release 并上传。
        # 公开仓库默认 GITHUB_TOKEN 即可（permissions 已声明 contents: write）。
        run: |
          dotnet tool install -g vpk
          vpk download github --repoUrl https://github.com/FM1503/WinQuick
          vpk pack -u WinQuick -v ${{ steps.ver.outputs.version }} -p publish --channel win-x64 --mainExe WinQuick.exe
          vpk upload github --repoUrl https://github.com/FM1503/WinQuick --publish --releaseName "WinQuick ${{ steps.ver.outputs.version }}" --tag $env:GITHUB_REF_NAME
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

## 第 3 步：验证（AC4）
1. `head -2 LICENSE` → 含 GNU GENERAL PUBLIC LICENSE / Version 3；
2. YAML 解析：`python -c "import yaml,io; yaml.safe_load(io.open('.github/workflows/release.yml', encoding='utf-8')); print('yaml ok')"`（无 python/pyyaml 则注明跳过）；
3. vpk 本地演练（不 upload）：
   a. `dotnet publish src/WinQuick.App/WinQuick.App.csproj -c Release -r win-x64 --self-contained -o publish --nologo` → 0 错误；
   b. `dotnet tool install -g vpk`（已装则 update，输出忽略）；
   c. `vpk pack -u WinQuick -v 1.1.0 -p publish --channel win-x64 --mainExe WinQuick.exe` → 无致命错误（首跑警告找不到上一版 release 属正常，无 delta 不算失败）；
   d. `ls Releases` → 含 WinQuick-Setup.exe 与 releases.win-x64.json（文件名以实际输出为准）。
