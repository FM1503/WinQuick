# 发布 WinQuick。
#
# 用法:
#   tools/publish.ps1                     自包含单文件 -> publish/
#   tools/publish.ps1 -FrameworkDependent 框架依赖（需目标机装 .NET 桌面运行时）
#
# ⚠ 绝对不要开 EnableCompressionInSingleFile。
#
# 实测（同一台机器，静置 12 秒后取任务管理器口径「活动的专用工作集」）：
#
#   方案                      磁盘      内存
#   自包含单文件 + 压缩        59 MB     128.5 MB   ← 内存翻倍
#   自包含单文件 不压缩        125 MB     66.4 MB
#   自包含散装目录            133 MB     65.4 MB
#   框架依赖                  ~1 MB      67.2 MB
#
# 原因：压缩后的程序集**无法被内存映射**，运行时必须把它解压到私有内存里，
# 于是私有工作集大涨。压缩是拿内存换磁盘的开关 —— 对常驻程序这是完全错误的取舍：
# 它花 62 MB 常驻内存去省 66 MB 磁盘。而本项目的内存目标是 60 MB 量级，
# 开了压缩直接翻倍到 128 MB。
#
# 所以默认关压缩：单文件 125 MB，换取内存达标。要是嫌大，用 -FrameworkDependent。

param(
    [switch]$FrameworkDependent,
    [string]$Output = "publish"
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\WinQuick.App\WinQuick.App.csproj'
$outDir = Join-Path $PSScriptRoot "..\$Output"

$common = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $outDir,
    # 关掉调试符号：它会让单文件包里多出一份体积，而发布版不需要。
    '-p:DebugType=none'
)

if ($FrameworkDependent) {
    Write-Host '发布方式：框架依赖（目标机需安装 .NET 桌面运行时 10）'
    dotnet @common -p:PublishSingleFile=false | Out-Host
}
else {
    Write-Host '发布方式：自包含单文件（不压缩，见文件头的实测说明）'
    dotnet @common `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true | Out-Host
}

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码 $LASTEXITCODE"
}

$exe = Join-Path $outDir 'WinQuick.exe'
if (-not (Test-Path $exe)) { throw "没有产出 $exe" }

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ''
Write-Host "完成：$exe（$size MB）"

if (-not $FrameworkDependent) {
    Write-Host '提示：产物是单个 exe，不依赖 .NET 运行时，也不需要额外文件。'
}
