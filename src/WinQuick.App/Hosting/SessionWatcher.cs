using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Hosting;

/// <summary>
/// 会话状态监听（锁屏 / 解锁 / 切换用户）。
///
/// 为什么必须处理：低级键盘钩子只在同一个 desktop 上生效，锁屏与 UAC 安全桌面
/// 是另一个 desktop，期间收不到任何输入。解锁回来之后必须
/// ①重装钩子（它可能已被系统摘掉）②清空按键状态（锁屏时按下的修饰键会留下"卡住"的
/// 假状态，导致后续判断全错）③强制关闭可能残留的覆盖层。
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    private readonly HwndSource _messageWindow;
    private bool _disposed;

    public SessionWatcher()
    {
        var parameters = new HwndSourceParameters("WinQuick.Session")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)WINDOW_STYLE.WS_POPUP),
            ExtendedWindowStyle = 0,
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        // NOTIFY_FOR_THIS_SESSION 是宏，CsWin32 不生成；值为 0。
        const uint NOTIFY_FOR_THIS_SESSION = 0;
        PInvoke.WTSRegisterSessionNotification(
            (HWND)_messageWindow.Handle, NOTIFY_FOR_THIS_SESSION);
    }

    /// <summary>屏幕已锁定。应当立即收敛覆盖层，不要留在屏上。</summary>
    public event EventHandler? SessionLocked;

    /// <summary>屏幕已解锁。应当重装钩子、清空按键状态、强制关闭残留覆盖层。</summary>
    public event EventHandler? SessionUnlocked;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != PInvoke.WM_WTSSESSION_CHANGE) return IntPtr.Zero;

        const int WTS_SESSION_LOCK = 0x7;
        const int WTS_SESSION_UNLOCK = 0x8;
        const int WTS_CONSOLE_CONNECT = 0x1;
        const int WTS_SESSION_DESKTOP_READY = 0xF;

        switch (wParam.ToInt32())
        {
            case WTS_SESSION_LOCK:
                SessionLocked?.Invoke(this, EventArgs.Empty);
                break;

            case WTS_SESSION_UNLOCK:
            case WTS_SESSION_DESKTOP_READY:
            case WTS_CONSOLE_CONNECT:
                SessionUnlocked?.Invoke(this, EventArgs.Empty);
                break;
        }

        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 销毁窗口前必须注销，否则会收到投递到已释放窗口的通知。
        PInvoke.WTSUnRegisterSessionNotification((HWND)_messageWindow.Handle);
        _messageWindow.Dispose();
    }
}
