using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using Windows.Win32;

namespace WinQuick.App.Design;

/// <summary>
/// 系统主题监听（spec skin-formalization §5）：明暗模式 + 强调色。
///
/// 读注册表而不是 WinRT UISettings：WPF 直调 WinRT 要引入 CsWinRT 投影，
/// 而这两个值注册表就是权威真源（设置APP 写的就是它们）；
/// WM_SETTINGCHANGE(lParam="ImmersiveColorSet") 是系统切换主题时的标准广播。
/// 消息窗口范式照抄 SessionWatcher（同类需求同一写法，排查时心智一份）。
/// </summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private readonly HwndSource _messageWindow;
    private bool _disposed;

    public SystemThemeWatcher()
    {
        var parameters = new HwndSourceParameters("WinQuick.SystemTheme")
        {
            Width = 1, Height = 1,
            PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP),
            ExtendedWindowStyle = 0,
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
    }

    /// <summary>系统明暗或强调色变化。UI 线程消息泵内触发。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>当前是否浅色应用模式（AppsUseLightTheme=1）。读不到按深色（本项目基线即深色）。</summary>
    public static bool IsLightMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }

    /// <summary>
    /// 系统强调色（#RRGGBB）。AccentColor 是 0xAABBGGRR 的 DWORD；
    /// 读不到时按 Fluent 官方默认（浅色 #0078D4 / 深色 #4CC2FF）。
    /// </summary>
    public static string AccentColor()
    {
        var fallback = IsLightMode() ? "#0078D4" : "#4CC2FF";
        using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
        // 先 AccentColor，读不到再试 ColorizationColor（玻璃色，少数机型才只有它）；
        // 两个键同是 0xAABBGGRR 的 DWORD，换算一致。?? 链比嵌套 is 模式直白——
        // 后者在短路位置连模式变量都过不了 definite assignment（上一轮编译失败教训）。
        var raw = key?.GetValue("AccentColor") ?? key?.GetValue("ColorizationColor");
        if (raw is not int argb) return fallback;
        var r = argb & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = (argb >> 16) & 0xFF;
        // 过黑过白都不是可用的强调色（透明度残留或异常值），退回官方默认。
        if (r + g + b < 0x18 || r + g + b > 0x2E0) return fallback;
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE &&
            Marshal.PtrToStringUni(lParam) is "ImmersiveColorSet")
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messageWindow.Dispose();
    }
}
