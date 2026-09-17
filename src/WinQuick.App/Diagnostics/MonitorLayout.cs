using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Diagnostics;

public sealed record MonitorInfo(
    int Index,
    bool IsPrimary,
    int X, int Y, int Width, int Height,
    uint Dpi)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public double Scale => Dpi / 96.0;
    public override string ToString() =>
        $"#{Index} ({(IsPrimary ? "主" : "副")}) ({X},{Y}) {Width}x{Height} @{Dpi}dpi";
}

/// <summary>
/// 显示器拓扑。
///
/// 为什么每屏一个窗口，而不是一个窗口横跨所有屏：
/// 官方定义下「一个 HWND 只有一个 DPI」—— 取窗口主要所在显示器的 DPI。
/// 所以横跨两块不同缩放的屏幕时，必然会有一半内容物理尺寸错误，
/// 而且窗口跨过"主要面积"临界点时会反复触发 WM_DPICHANGED，处理不当会陷入死循环。
/// 每屏一个窗口是唯一干净的解法，也正好匹配"每屏一块遮罩"的需求。
///
/// 所有坐标都是物理像素。取数必须在 PerMonitorV2 进程里做才准确。
/// </summary>
public static class MonitorLayout
{
    public static unsafe IReadOnlyList<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>(4);
        var index = 0;

        PInvoke.EnumDisplayMonitors(default, null,
            (HMONITOR hMonitor, HDC _, RECT* _, LPARAM _) =>
            {
                var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
                if (!PInvoke.GetMonitorInfo(hMonitor, ref info))
                    return true;   // 拿不到就跳过这块屏，不该因为一块异常显示器整体失败

                // 运行时守卫而不是注释：GetDpiForMonitor 要 Win8.1+，而分析器只认这种写法
                //（这也正是它想要的东西 —— "可证安全"而不是"我保证"）。产品要求 Win10，
                // 所以下面这条分支实际上到不了；留着是为了让"不支持的平台上不会调它"可被静态证明。
                var dpi = 96u;
                if (OperatingSystem.IsWindowsVersionAtLeast(8, 1))
                    PInvoke.GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpi, out _);

                var rc = info.rcMonitor;
                result.Add(new MonitorInfo(
                    index++,
                    (info.dwFlags & 1) != 0,          // MONITORINFOF_PRIMARY
                    rc.left, rc.top,
                    rc.right - rc.left, rc.bottom - rc.top,
                    dpi));

                return true;
            },
            default);

        return result;
    }

    /// <summary>光标所在的显示器；拿不到时退化为第一块屏。</summary>
    public static unsafe MonitorInfo AtCursor()
    {
        var monitors = All();
        if (monitors.Count == 0)
            return new MonitorInfo(0, true, 0, 0, 1920, 1080, 96);

        if (PInvoke.GetCursorPos(out var pt))
        {
            var hmon = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
            var dpi = 96u;
            if (OperatingSystem.IsWindowsVersionAtLeast(8, 1))
                PInvoke.GetDpiForMonitor(hmon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpi, out _);

            // 用光标所在屏的 DPI 反查是哪个 index（EnumDisplayMonitors 的顺序是稳定的）
            foreach (var m in monitors)
                if (pt.X >= m.X && pt.X < m.Right && pt.Y >= m.Y && pt.Y < m.Bottom)
                    return m with { Dpi = dpi };
        }

        foreach (var m in monitors)
            if (m.IsPrimary) return m;

        return monitors[0];
    }

    public static bool TryGetCursorPosition(out int x, out int y)
    {
        if (PInvoke.GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
            return true;
        }
        x = y = 0;
        return false;
    }

    /// <summary>
    /// 把边长为 size（物理像素）的窗口放到光标处，并把整个窗口钳制在目标屏内。
    ///
    /// 钳制是必须的：靠近屏幕边缘时若不钳制，圆盘会有一部分跑到屏幕外，
    /// 而扇区角度保持绝对固定（不随钳制旋转），否则肌肉记忆会被破坏。
    /// </summary>
    public static (int X, int Y) ClampToMonitor(int cursorX, int cursorY, int size, MonitorInfo monitor)
    {
        var left = cursorX - size / 2;
        var top = cursorY - size / 2;

        left = Math.Clamp(left, monitor.X, Math.Max(monitor.X, monitor.Right - size));
        top = Math.Clamp(top, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - size));

        return (left, top);
    }
}
