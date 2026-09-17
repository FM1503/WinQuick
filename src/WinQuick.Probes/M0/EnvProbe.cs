using System.IO;
using System.Text.Json;
using WinQuick.App.Diagnostics;

namespace WinQuick.App.M0;

/// <summary>
/// M0.1 环境报告。
///
/// 存在的理由：我最初用 PowerShell 5.1 测显示器配置，拿到 1755x1097 @ 96DPI ——
/// 那是 DPI-unaware 进程被系统虚拟化后的假值。本进程声明了 PerMonitorV2，
/// 这里输出的才是真实分辨率与 DPI，而这两个数决定遮罩层尺寸和轮盘缩放系数。
/// </summary>
public static class EnvProbe
{
    public static void Run()
    {
        ProbeOutput.Initialize("env");

        ProbeOutput.Section("运行时");
        foreach (var line in NativeDisplay.DescribeEnvironment().Split('\n'))
            ProbeOutput.Line(line);

        ProbeOutput.Section("渲染能力");
        ProbeOutput.Line("  " + NativeDisplay.RenderTierDescription());

        ProbeOutput.Section("显示器与 DPI（本进程 DPI-aware，数值为真值）");
        var snap = NativeDisplay.Capture();
        ProbeOutput.Kv("主屏物理分辨率", $"{snap.ScreenWidth} x {snap.ScreenHeight}");
        ProbeOutput.Kv("虚拟屏原点", $"({snap.VirtualX}, {snap.VirtualY})");
        ProbeOutput.Kv("虚拟屏尺寸", $"{snap.VirtualWidth} x {snap.VirtualHeight}");
        ProbeOutput.Kv("显示器数量", snap.MonitorCount);
        ProbeOutput.Kv("光标所在屏 DPI", $"{snap.DpiAtCursor} ({snap.ScaleAtCursorPercent}%)");

        ProbeOutput.Section("遮罩层尺寸推算（每屏一个窗口，用物理像素）");
        var pixels = (long)snap.VirtualWidth * snap.VirtualHeight;
        ProbeOutput.Kv("虚拟屏总像素", $"{pixels:N0}");
        ProbeOutput.Kv("对比 4K 全屏像素", $"8,294,400（本机为 {pixels * 100.0 / 8294400:0.#}%）");
        ProbeOutput.Line();
        ProbeOutput.Line("  结论：单显示器 + 100% 缩放时，跨屏 DPI 与「一个 HWND 只有一个 DPI」");
        ProbeOutput.Line("        这两个坑在本机无法复现，需要在多屏/混合 DPI 的机器上另行验证。");

        ProbeOutput.Section("WPF 资源字典与主题");
        ProbeOutput.Kv("SystemParameters 主题", DetectTheme());

        ProbeOutput.Flush();
    }

    private static string DetectTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var apps = key?.GetValue("AppsUseLightTheme");
            var sys = key?.GetValue("SystemUsesLightTheme");
            var transparency = key?.GetValue("EnableTransparency");
            return $"Apps={(apps is 1 ? "Light" : "Dark")}, System={(sys is 1 ? "Light" : "Dark")}, " +
                   $"Transparency={(transparency is 0 ? "Off" : "On")}";
        }
        catch (Exception ex)
        {
            return "(读取失败: " + ex.Message + ")";
        }
    }
}
