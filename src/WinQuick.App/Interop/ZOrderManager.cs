using WinQuick.App.Diagnostics;
using Windows.Win32.UI.Accessibility;
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
    /// <summary>实际执行重申的次数。用于核对"静置时是否还在反复重申"。</summary>
    public int ReassertCount { get; private set; }

    /// <summary>廉价检查判定"不需要重申"而跳过的次数。</summary>
    public int SkippedCount { get; private set; }

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
    /// 廉价检查「是否真的需要重申」。
    ///
    /// 只检查两件**真会发生**的事，不检查「有没有别人的 topmost 窗口压在我们上面」——
    /// 后者在本机实测恒为真（任务栏、输入法、各种 shell 浮层都是 topmost，实测 42 个），
    /// 拿它当判据等于每次都重申，也就是没有省 —— 而无条件重申会让分层窗口
    /// 持续重新合成，观感上就是**持续闪烁**（用户报的现象）。
    ///
    /// 所以：
    /// 1. **置顶位还在吗** —— 样式被系统清掉是真实发生过的情况（PowerToys 的
    ///    AlwaysOnTop 模块专门为 issue #17332 / #19168 写了重设逻辑）。
    /// 2. **我们内部的相对顺序还对吗** —— 这个工具自己的重建路径（改透明度、换菜单、
    ///    显示器变化）会新建窗口、把新窗口插到 topmost 最前面，从而打乱内部顺序。
    ///    这正是之前"轮盘与面板点不到"的根因，必须留住这条检查。
    ///
    /// 「别人压上来」改由前台变更事件驱动 —— 见 <see cref="WatchForeground"/>。
    /// PowerToys 也是这么做的（`SetWinEventHook` + 「检测到 topmost 被清掉才重设」）。
    /// </summary>
    public bool NeedsReassert()
    {
        var layers = new List<IReadOnlyList<nint>>(_layers.Count);
        foreach (var (_, source) in _layers) layers.Add(source());

        var ours = new HashSet<nint>();
        foreach (var layer in layers)
            foreach (var hwnd in layer)
                if (hwnd != 0) ours.Add(hwnd);

        if (ours.Count == 0) return false;

        // ① 置顶位还在吗
        foreach (var hwnd in ours)
        {
            if (!PInvoke.IsWindow((HWND)hwnd)) continue;

            var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(
                (HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            if (!ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST)) return true;
        }

        // ② 我们内部的相对顺序还对吗
        //    沿顶层窗口链走一遍，把我们各层窗口出现的位置记下来，
        //    与层级栈声明的顺序比对（栈索引越小应当越靠上）。
        var seen = new Dictionary<nint, int>();
        var hwnd2 = PInvoke.GetTopWindow(default);
        var guard = 0;
        while (hwnd2 != default && guard++ < 5000)
        {
            if (ours.Contains((IntPtr)hwnd2)) seen[(IntPtr)hwnd2] = guard;

            // 收齐了就提前退出，不必走完整条链。
            if (seen.Count == ours.Count) break;

            hwnd2 = PInvoke.GetWindow(hwnd2, GET_WINDOW_CMD.GW_HWNDNEXT);
        }

        var expected = new List<nint>();
        for (var i = 0; i < layers.Count; i++)
            foreach (var hwnd in layers[i])
                if (hwnd != 0 && seen.ContainsKey(hwnd)) expected.Add(hwnd);

        var actual = expected.OrderBy(h => seen[h]).ToList();

        if (expected.SequenceEqual(actual))
        {
            SkippedCount++;
            return false;
        }

        return true;
    }

    // ---------------------------------------------------------------- 前台变更监听

    private WINEVENTPROC? _foregroundProc;
    private UnhookWinEventSafeHandle? _foregroundHook;
    private long _lastEventReassertMs;

    /// <summary>
    /// 订阅前台窗口变更事件，在「别人抢到前台」时重申一次置顶。
    ///
    /// 这是替代「无条件定时重申」的做法：只在真的可能发生时动手，
    /// 平时一次 SetWindowPos 都不发。
    /// 用 `(uint)(PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS)` —— 不注入目标进程，
    /// 且忽略我们自己进程的事件（否则会话切换/设置面板激活都会触发）。
    /// </summary>
    public void WatchForeground()
    {
        if (_foregroundHook is not null) return;

        _foregroundProc = OnForegroundChanged;

        _foregroundHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            PInvoke.EVENT_SYSTEM_FOREGROUND,
            default,
            _foregroundProc,
            0, 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
    }

    private void OnForegroundChanged(
        HWINEVENTHOOK hook, uint eventId, HWND hwnd,
        int idObject, int idChild, uint threadId, uint time)
    {
        // 节流：切换前台时事件可能密集触发，没必要每次都重申。
        var now = Environment.TickCount64;
        if (now - _lastEventReassertMs < 250) return;
        _lastEventReassertMs = now;

        Reassert();
    }

    public void StopWatching()
    {
        if (_foregroundHook is null) return;

        // 用 SafeHandle 自己的 Dispose 而不是 UnhookWinEvent —— CsWin32 为它生成了
        // SafeHandle 包装，手动调用 API 反而会漏掉引用计数。
        _foregroundHook.Dispose();
        _foregroundHook = null;
        _foregroundProc = null;
    }

    /// <summary>
    /// 重申整个层级栈。
    ///
    /// 从下往上逐个插到 topmost 最前 —— 这样最后一次插入的（栈顶）确实在最前面，
    /// 而不会出现「谁最后被调谁就在最上面」的随机结果。
    /// </summary>
    public void Reassert()
    {
        ReassertCount++;

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
