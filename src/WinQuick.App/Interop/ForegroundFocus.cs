using System.Windows;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Interop;

/// <summary>
/// 前台窗口的存档与还原。
///
/// 为什么需要专门一层：
/// - 轮盘要接收键盘（Esc、方向键），就必须拿焦点；拿焦点就会顶掉用户原来的前台窗口。
/// - 所以显示前存档，关闭时还原 —— 用户的输入焦点不能因为用过一次轮盘就丢了。
///
/// 有一条关键区别（调研里查到的官方行为）：
/// **在 WM_HOTKEY 的处理函数里同步调用 SetForegroundWindow 是被允许的**，
/// 因为注册热键的进程会被视为「收到了最后一次输入事件」，满足前台权限的六个条件之一。
/// 但这条路**必须同步**：一旦 await、切线程或者 PostMessage 之后再调，权限就丢了。
///
/// 低级键盘钩子路径没有这个豁免（钩子不产生输入事件的所有权），
/// 所以额外准备 AttachThreadInput 兜底。
/// </summary>
public sealed class ForegroundFocus
{
    private HWND _previous;
    private bool _hasSaved;

    /// <summary>显示覆盖层之前调用。记录当前前台窗口，供之后还原。</summary>
    public void Save()
    {
        _previous = PInvoke.GetForegroundWindow();
        _hasSaved = true;
    }

    /// <summary>
    /// 把焦点交给指定窗口。
    /// 先直接调；失败再走 AttachThreadInput 挂到前台线程的输入队列上重试。
    /// 挂队列有卡死风险，所以只在必要时做，且立刻解开。
    /// </summary>
    internal static unsafe void AttachTo(HWND target)
    {
        if (PInvoke.SetForegroundWindow(target)) return;

        var foreground = PInvoke.GetForegroundWindow();
        var targetThread = PInvoke.GetWindowThreadProcessId(target, null);
        var foregroundThread = foreground != default
            ? PInvoke.GetWindowThreadProcessId(foreground, null)
            : 0;

        if (targetThread == 0 || foregroundThread == 0 || targetThread == foregroundThread)
            return;

        if (PInvoke.AttachThreadInput(foregroundThread, targetThread, true))
        {
            try
            {
                PInvoke.SetForegroundWindow(target);
                PInvoke.SetFocus(target);
            }
            finally
            {
                PInvoke.AttachThreadInput(foregroundThread, targetThread, false);
            }
        }
    }

    /// <summary>关闭覆盖层之后调用，把前台窗口还给用户原来的那个。</summary>
    public void Restore()
    {
        if (!_hasSaved) return;
        _hasSaved = false;

        var target = _previous;
        _previous = default;

        // 原窗口可能已经关掉了（用户在这期间关了它），所以要判存活。
        if (target == default || !PInvoke.IsWindow(target)) return;

        // **别把焦点交给另一个虚拟桌面上的窗口。**
        //
        // SetForegroundWindow 到别的桌面会让 Windows 把用户切过去 ——
        // 那是用户实测报的"用了轮盘反而被拽回工具所在的桌面"。
        // 触发场景很具体：覆盖层在桌面 A 上打开，用户切到桌面 B（覆盖层随即被收起），
        // 收起时这一步归还焦点就会把用户拽回 A。
        //
        // 判据是"目标窗口在不在**当前**桌面"，不是"和我们一不一样"——
        // 后者在这种情况下两边都在 A 上，判不出来。
        if (!VirtualDesktop.IsOnCurrentDesktop((nint)target)) return;

        PInvoke.SetForegroundWindow(target);
    }

    /// <summary>把焦点交给 WPF 窗口，并返回它的 HWND。</summary>
    internal static HWND AttachTo(Window window)
    {
        var hwnd = (HWND)new WindowInteropHelper(window).Handle;
        if (hwnd != default) AttachTo(hwnd);
        return hwnd;
    }
}
