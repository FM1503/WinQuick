$ErrorActionPreference = 'Continue'

$lnk = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\OBS Studio.lnk"
$logDir = "$env:APPDATA\obs-studio\logs"
$sentinel = "$env:APPDATA\obs-studio\.sentinel"

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr p);
    public static List<string> TopLevel(uint target) {
        var found = new List<string>();
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != target) return true;
            var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            found.Add("class=" + c.ToString() + "  visible=" + IsWindowVisible(h) + "  text=[" + t.ToString() + "]");
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@

function Kill-And-Clean {
    Get-Process obs64 -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    # 强杀会重新写下"异常退出"标记；清掉，免得把用户的 OBS 留在安全模式询问状态
    Remove-Item $sentinel -Force -ErrorAction SilentlyContinue
}

Kill-And-Clean
Write-Host ("开始前 .sentinel 是否存在: " + (Test-Path $sentinel))

function Launch-And-Inspect([string]$label, [string]$cwd) {
    Kill-And-Clean
    if ($cwd) { Set-Location $cwd }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $lnk
    $psi.UseShellExecute = $true
    [System.Diagnostics.Process]::Start($psi) | Out-Null
    Start-Sleep -Seconds 10

    $proc = Get-Process obs64 -ErrorAction SilentlyContinue | Select-Object -First 1
    Write-Host "--- $label"
    Write-Host ("    进程还在: " + [bool]$proc)

    if ($proc) {
        Write-Host '    它的顶层窗口:'
        [Win]::TopLevel($proc.Id) | ForEach-Object { Write-Host ("      " + $_) }
    }

    $f = Get-ChildItem $logDir -Filter *.txt -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f) {
        $lines = Get-Content $f.FullName
        Write-Host ("    日志 " + $f.Name + " 共 " + $lines.Count + " 行；开头 3 行:")
        $lines | Select-Object -First 3 | ForEach-Object { Write-Host ("      " + $_) }
    }
}

Write-Host ''
Write-Host '=== 清掉标记后：从我们这条 .lnk 启动（当前目录 = D:\Work\WinQuick） ==='
Launch-And-Inspect '.lnk（正常目录）' 'D:\Work\WinQuick'

Write-Host ''
Write-Host '=== 清掉标记后：从 C:\Windows\System32 启动同一条 .lnk ==='
Launch-And-Inspect '.lnk（System32 目录）' 'C:\Windows\System32'

Kill-And-Clean
Write-Host ''
Write-Host ("结束后 .sentinel 是否存在: " + (Test-Path $sentinel) + "（不存在 = 已清干净）")
