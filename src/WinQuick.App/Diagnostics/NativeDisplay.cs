using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Diagnostics;

/// <summary>
/// 显示器与 DPI 取数。
///
/// 重要前提：这些数值只有在 PerMonitorV2 进程里才是真值。
/// DPI-unaware 进程（例如 Windows PowerShell 5.1）拿到的全是系统虚拟化后的值，
/// 我最初用 PowerShell 测出的 1755x1097 就是这么来的假数据。
///
/// M0 只用标量 API：单显示器场景下已经完整，且不碰 MONITORINFOEXW 的结构体布局。
/// 多显示器拓扑（EnumDisplayMonitors）在 M1 引入窗口池时再加。
/// </summary>
public static class NativeDisplay
{
    public sealed record Snapshot(
        int ScreenWidth, int ScreenHeight,
        int VirtualX, int VirtualY, int VirtualWidth, int VirtualHeight,
        int MonitorCount,
        uint DpiAtCursor, int ScaleAtCursorPercent,
        string WindowDpiSource, double WpfDpiScaleX, double WpfDpiScaleY);

    public static unsafe Snapshot Capture(Window? dpiSourceWindow = null)
    {
        var w = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        var h = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        var vx = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var vy = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var vw = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        var vh = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        var monitors = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CMONITORS);

        // 光标所在屏的 DPI：PMv2 下每屏可能不同，这是轮盘该用哪个缩放系数的依据。
        // handle 为空时 GetDpiForMonitor 会失败、dx 保持 0，由下面的 cursorDpi==0 分支兜住，
        // 所以这里不必显式判空（也就避开了 CsWin32 句柄类型的判空写法差异）。
        uint cursorDpi = 0;
        if (PInvoke.GetCursorPos(out var pt))
        {
            var hmon = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            // MDT_EFFECTIVE_DPI = 0
            PInvoke.GetDpiForMonitor(hmon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dx, out _);
            cursorDpi = dx;
        }

        var (src, scaleX, scaleY) = ("(no window)", 0d, 0d);
        if (dpiSourceWindow is not null)
        {
            var handle = new WindowInteropHelper(dpiSourceWindow).Handle;
            if (handle != IntPtr.Zero)
            {
                var winDpi = PInvoke.GetDpiForWindow((HWND)handle);
                var vd = VisualTreeHelper.GetDpi(dpiSourceWindow);
                src = $"GetDpiForWindow={winDpi} ({(winDpi == 0 ? 0 : winDpi * 100 / 96)}%), " +
                      $"VisualTreeHelper.DpiScale=({vd.DpiScaleX:0.###},{vd.DpiScaleY:0.###})";
                scaleX = vd.DpiScaleX;
                scaleY = vd.DpiScaleY;
            }
        }

        return new Snapshot(
            w, h, vx, vy, vw, vh, monitors,
            cursorDpi, cursorDpi == 0 ? 0 : (int)(cursorDpi * 100 / 96),
            src, scaleX, scaleY);
    }

    /// <summary>
    /// WPF 渲染层级。这是本次测量能否外推到用户真实使用场景的前提：
    /// Tier 0 = 软件渲染（WARP），此时所有帧率数字都不可信、且透明窗口会非常慢。
    /// </summary>
    public static string RenderTierDescription()
    {
        var tier = RenderCapability.Tier >> 16;
        var name = tier switch
        {
            0 => "0 - 软件渲染 (WARP)。帧率数据不可信；透明窗口会极慢",
            1 => "1 - 部分硬件加速（视频/位图有加速，3D 无）",
            2 => "2 - 完整硬件加速",
            _ => $"{tier} - 未知",
        };
        return $"Tier {name}  |  PS2.0={RenderCapability.IsPixelShaderVersionSupported(2, 0)}, " +
               $"PS3.0={RenderCapability.IsPixelShaderVersionSupported(3, 0)}, " +
               $"ShaderEffect软件回退={RenderCapability.IsShaderEffectSoftwareRenderingSupported}";
    }

    public static string DescribeEnvironment()
    {
        var d = new System.Text.StringBuilder();
        d.AppendLine($"  OS                : {Environment.OSVersion}  (64bit={Environment.Is64BitOperatingSystem})");
        d.AppendLine($"  Runtime           : {Environment.Version}");
        d.AppendLine($"  Process           : 64bit={Environment.Is64BitProcess}, CPU={Environment.ProcessorCount}, " +
                       $"ServerGC={System.Runtime.GCSettings.IsServerGC}, " +
                       $"LatencyMode={System.Runtime.GCSettings.LatencyMode}");
        return d.ToString().TrimEnd();
    }
}
