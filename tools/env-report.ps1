# Records the runtime environment: OS build, RAM, monitor topology, per-monitor DPI.
# ASCII-only on purpose: Windows PowerShell 5.1 decodes UTF-8-no-BOM files as ANSI,
# which mangles non-ASCII comments and can break here-string parsing.
$ErrorActionPreference = 'SilentlyContinue'

$os = Get-CimInstance Win32_OperatingSystem
Write-Output "=== OS ==="
Write-Output ("Caption      : " + $os.Caption)
Write-Output ("Build        : " + $os.BuildNumber)
Write-Output ("Version      : " + $os.Version)
Write-Output ("Architecture : " + $os.OSArchitecture)
Write-Output ("TotalRAM(GB) : " + [math]::Round($os.TotalVisibleMemorySize / 1MB, 1))
Write-Output ("FreeRAM(GB)  : " + [math]::Round($os.FreePhysicalMemory / 1MB, 1))

# Monitor topology + per-monitor DPI. Under PerMonitorV2 this is the only correct
# way to get physical-pixel rects and per-screen DPI (GetSystemMetrics returns
# primary-monitor values for most indices).
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Mon {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprc, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);

    public static List<string> Describe() {
        var outp = new List<string>();
        int idx = 0;
        MonitorEnumProc cb = delegate(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            GetMonitorInfo(hMon, ref mi);
            uint dx = 0, dy = 0;
            int hr = GetDpiForMonitor(hMon, 0, out dx, out dy);  // 0 = MDT_EFFECTIVE_DPI
            bool primary = (mi.dwFlags & 1) != 0;
            int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            int pct = (dx == 0) ? 0 : (int)(dx * 100 / 96);
            outp.Add(String.Format(
                "  [{0}] {1}  primary={2}  monitor=({3},{4}) {5}x{6}  effectiveDPI={7} ({8}%)  hresult={9}",
                idx, mi.szDevice.TrimEnd('\0'), primary,
                mi.rcMonitor.Left, mi.rcMonitor.Top, w, h, dx, pct, hr));
            idx++;
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);

        outp.Add(String.Format("  virtual screen = ({0},{1}) {2}x{3}  monitors={4}",
            GetSystemMetrics(76), GetSystemMetrics(77),
            GetSystemMetrics(78), GetSystemMetrics(79),
            GetSystemMetrics(80)));
        return outp;
    }
}
'@

Write-Output ""
Write-Output "=== Displays ==="
foreach ($line in [Mon]::Describe()) { Write-Output $line }

Write-Output ""
Write-Output "=== Dotnet SDKs ==="
foreach ($line in (dotnet --list-sdks)) { Write-Output ("  " + $line) }

Write-Output ""
Write-Output "=== Widgets (Win11; determines whether Win+W is claimed) ==="
$w = Get-AppxPackage -Name 'MicrosoftWindows.Client.WebExperience'
if ($w) { Write-Output ("  INSTALLED: " + $w.Version) } else { Write-Output "  not installed" }

$dsh = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' -ErrorAction SilentlyContinue
if ($dsh) { Write-Output ("  HKLM\...\Dsh\AllowNewsAndInterests = " + $dsh.AllowNewsAndInterests) }
else { Write-Output "  HKLM\...\Dsh\AllowNewsAndInterests = (not set)" }

Write-Output ""
Write-Output "=== ForegroundLockTimeout ==="
$flt = Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name ForegroundLockTimeout -ErrorAction SilentlyContinue
if ($flt) { Write-Output ("  = " + $flt.ForegroundLockTimeout) } else { Write-Output "  = (not set)" }
