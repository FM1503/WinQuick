$ErrorActionPreference = 'Stop'

Get-Process obs64 -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$temp = Join-Path $env:TEMP ('wq-cwd-' + [Guid]::NewGuid().ToString('N'))
$marker = Join-Path $temp '起始位置标记目录'
New-Item -ItemType Directory -Path $marker -Force | Out-Null

$link = Join-Path $temp 'test.lnk'
$report = Join-Path $temp 'cwd.txt'

# 建一条快捷方式：目标是 cmd.exe，让它把自己的工作目录写进文件。
# 只有"创建"这一步用 COM；被测的是**启动**路径。
$sh = New-Object -ComObject WScript.Shell
$sc = $sh.CreateShortcut($link)
$sc.TargetPath = "$env:SystemRoot\System32\cmd.exe"
$sc.Arguments = '/c cd > "' + $report + '"'
$sc.WorkingDirectory = $marker
$sc.Save()

Write-Host "测试快捷方式: $link"
Write-Host "  起始位置 = $marker"
Write-Host ''

# 只返回值，叙述一律走 Write-Host。
# 上一版用 Write-Output 输出叙述、又 return 值，调用方一条赋值就把两者一起吃掉：
# 打印是空的、而数组比较非空即为真 —— 于是得出了"一致"的假结论。
function Get-LaunchedWorkingDirectory([string]$wd) {
    Remove-Item $report -ErrorAction SilentlyContinue

    # 与 ActionExecutor.StartProcess 完全相同的调用方式
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $link
    $psi.UseShellExecute = $true
    if ($wd) { $psi.WorkingDirectory = $wd }

    [System.Diagnostics.Process]::Start($psi) | Out-Null
    Start-Sleep -Seconds 2

    if (Test-Path $report) { return (Get-Content $report -Raw).Trim() }
    return '(没有写出目录)'
}

Write-Host '=== 子进程实际拿到的工作目录 ==='
$a = Get-LaunchedWorkingDirectory $null
Write-Host 'A) 不指定 WorkingDirectory（我们的现状）:'
Write-Host "     $a"

$b = Get-LaunchedWorkingDirectory $marker
Write-Host 'B) 显式指定 WorkingDirectory = 快捷方式的起始位置:'
Write-Host "     $b"

Write-Host ''
Write-Host '=== 判定 ==='
if ($a -eq $marker) {
    Write-Host '  >>> A 拿到了快捷方式自带的起始位置 —— 这一步是 shell 在做。'
    Write-Host '      那么"不带工作目录"不是原因，要另找别处。'
}
else {
    Write-Host '  >>> A **没有**拿到快捷方式自带的起始位置。'
    Write-Host '      这正是"外部能开、工具里报错"的成因：程序在错误的目录下被启动。'
}
if ($b -eq $marker) { Write-Host '  >>> B 证明显式指定起始位置有效 —— 修法就是补上它。' }

Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
