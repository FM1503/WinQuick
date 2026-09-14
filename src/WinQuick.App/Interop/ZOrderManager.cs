using WinQuick.App.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Interop;

/// <summary>
/// 我们自己的窗口层级栈。
///
/// ## 为什么需要它
///
/// 这个工具同时有多个顶层窗口（每屏遮罩 + 轮盘 + 操作条 + 设置面板）。
/// 之前每个窗口各自调一个 `ReassertTopmost()`，而那条实现是**空操作** ——
/// 它把 `hWndInsertAfter` 传成默认值、又带上了 `SWP_NOZORDER`。
/// 官方对 `SetWindowPos` 的说明是：
///
/// > A window can be made a topmost window either by setting the `hWndInsertAfter`
/// > parameter to `HWND_TOPMOST` **and ensuring that the `SWP_NOZORDER` flag is not set**…
///
/// `SWP_NOZORDER` 的语义是「保持当前 z 序、忽略插入位置」，所以那个调用什么也没做。
/// 后果：任何新建的窗口（比如重建遮罩）都会被插到 topmost 层的最前面、压住既有窗口，
/// 而我们再也没能把顺序恢复回来。实测复现：改一次遮罩深浅，轮盘与设置面板就都点不到了。
///
/// ## 做法
///
/// 把层级定义成**一个显式的栈**（索引 0 最靠上），重申时**从下往上**依次插到 topmost 最前。
/// 这样每次重申结束时，最终顺序确定等于这个栈，而不是取决于谁最后被创建。
///
/// 这与 PowerToys 的做法一致：MeasureTool 是「每屏一个 topmost 遮罩 + 一个 topmost 工具条」，
/// ColorPicker 把 z 序重申收敛到一个 helper。ZoomIt 是反面教材 ——
/// 它用 20~250ms 的定时器让多个 topmost 窗口互相重申，源码注释里全是
/// 「免得被另一个 topmost 窗口挤下去」。
/// </summary>
public sealed class ZOrderManager
{
    /// <summary>一层窗口。可以返回多个句柄（每屏一个遮罩）。</summary>
    public delegate IReadOnlyList<nint> LayerSource();

    // 索引 0 = 最靠上。重申时从后往前插，最后一个插入的就在最前。
    private readonly List<(string Name, LayerSource Source)> _layers = [];

    /// <summary>
    /// 定义层级。**必须按「从上到下」的顺序调用**，重申时会自动反序执行。
    /// </summary>
    public void AddLayer(string name, LayerSource source) => _layers.Add((name, source));

    public void Clear() => _layers.Clear();

    public IReadOnlyList<string> LayerNames => _layers.Select(l => l.Name).ToList();

    /// <summary>
    /// 重申整个层级栈。
    ///
    /// 从下往上逐个插到 topmost 最前 —— 这样最后一次插入的（栈顶）确实在最前面，
    /// 而不会出现「谁最后被调谁就在最上面」的随机结果。
    /// </summary>
    public void Reassert()
    {
        for (var i = _layers.Count - 1; i >= 0; i--)
        {
            foreach (var hwnd in _layers[i].Source())
            {
                if (hwnd == 0) continue;
                if (!PInvoke.IsWindow((HWND)hwnd)) continue;
                BringToTopmostFront((HWND)hwnd);
            }
        }
    }

    /// <summary>
    /// 把一个窗口插到 topmost 层的最前面。
    ///
    /// **不能带 `SWP_NOZORDER`** —— 带了的话 `hWndInsertAfter` 会被完全忽略，
    /// 这个调用就成了空操作（这正是之前的 bug）。
    /// 也不能带 `SWP_SHOWWINDOW`：那会绕过 WPF 的可见性状态（踩过这个坑）。
    /// </summary>
    internal static void BringToTopmostFront(HWND hwnd)
    {
        var ok = PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);

        if (!ok)
        {
            // 失败通常意味着窗口已销毁（重建过程中会遇到）。记下来便于排查，
            // 但不抛 —— z 序重申失败不该让整个流程中断。
            CrashLog.Write("z 序重申失败",
                new InvalidOperationException($"SetWindowPos(HWND_TOPMOST) 对 0x{(IntPtr)hwnd:X} 失败"));
        }
    }

    /// <summary>把一个窗口降到 topmost 之下（取消置顶）。给「需要临时让位」的场景用。</summary>
    internal static void Demote(HWND hwnd) =>
        PInvoke.SetWindowPos(hwnd, HWND.HWND_NOTOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
}
