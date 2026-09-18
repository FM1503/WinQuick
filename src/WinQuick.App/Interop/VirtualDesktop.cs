using System.Runtime.Versioning;
using WinQuick.App.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Interop;

/// <summary>
/// 虚拟桌面：让覆盖层的窗口跟着用户走。
///
/// ## 问题（用户实测报的两种现象）
///
/// **窗口属于"创建时所在的那个桌面"**，而 Windows **不会**因为 `Show()` 把它搬到当前桌面。
/// 这个工具的窗口是常驻的（开机时就创建、之后一直是停放态），所以用户切到别的桌面再按热键时：
///
/// 1. 只出遮罩（轮盘留在原桌面，而遮罩因为不激活地显示，看起来"出来了"却点不到、Esc 也收不掉）；
/// 2. 或者一按热键就被**拽回工具所在的那个桌面** —— 那是激活另一个桌面上的窗口的必然结果。
///
/// ## 修法
///
/// 1. 唤出前把我们的每个窗口搬到当前桌面（<see cref="MoveAllToCurrentDesktop"/>）；
/// 2. 覆盖层已经显示、用户却切走了桌面时，主动收起覆盖层（见 OverlayController 的定时检查）——
///    否则它会变成一堆"在别处"的窗口，既看不见也关不掉；
/// 3. 归还焦点时**不要**把焦点交给别的桌面上的窗口（那同样会把用户拽回去，见 <see cref="SameDesktop"/>）。
///
/// ## 为什么用 IVirtualDesktopManager 而不是那些 internal 接口
///
/// `IVirtualDesktopManager` 是**公开文档化**的 COM 接口（Win10 起），有微软文档与稳定 GUID，
/// 拿得到"这个窗口在哪个桌面"、"把它搬到哪个桌面"。而社区那些 `IVirtualDesktopManagerInternal`
/// 是未文档化的私有接口，**每版 Windows 的 vtable 都会变**（历史上多次因系统更新而崩）——
/// 一个常驻工具不该把自己的稳定性押在那上面。
///
/// 拿不到接口时（旧系统、被策略禁用）全部退化为"什么也不做"，绝不影响正常唤出。
/// </summary>
internal static class VirtualDesktop
{
    /// <summary>Windows 虚拟桌面管理器的 CLSID（公开）。</summary>
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private static readonly IVirtualDesktopManager? Manager = TryCreateManager();

    /// <summary>真的搬动过窗口的次数（诊断用：探针核对"换了桌面才搬"）。</summary>
    public static int MovedCount { get; private set; }

    /// <summary>省下来的次数（窗口已在当前桌面）。</summary>
    public static int SkippedCount { get; private set; }

    /// <summary>搬了但复验仍不在当前桌面的次数（不为 0 就说明这台机器上此路不通）。</summary>
    public static int FailedCount { get; private set; }

    public static bool IsSupported => IsAvailable && Manager is not null;

    /// <summary>
    /// 虚拟桌面管理器需要 Win10 10240 以上。
    ///
    /// 用 <see cref="SupportedOSPlatformGuardAttribute"/> 标出来（而不是在每处各写一遍
    /// <c>OperatingSystem.IsWindowsVersionAtLeast</c>）：分析器认得这个标注，
    /// 于是"守卫 + 使用"这件事只需要在一个地方说清楚 —— 与 P4 那次给 MonitorLayout
    /// 加 DPI 守卫同一个做法，也是分析器真正要的"可证安全"。
    /// </summary>
    [SupportedOSPlatformGuard("windows10.0.10240")]
    private static bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240);

    private static IVirtualDesktopManager? TryCreateManager()
    {
        if (!IsAvailable) return null;

        try
        {
            // CLSCTX_INPROC_SERVER：虚拟桌面管理器是个进程内 COM 对象。
            var hr = PInvoke.CoCreateInstance(
                ClsidVirtualDesktopManager, null, CLSCTX.CLSCTX_INPROC_SERVER,
                out IVirtualDesktopManager manager);

            return hr.Succeeded ? manager : null;
        }
        catch
        {
            // 拿不到就算了 —— 虚拟桌面不可用不该让唤出失败。
            return null;
        }
    }

    /// <summary>
    /// 把一组窗口搬到"用户此刻正在用的那个桌面"。返回真正搬动的个数。
    ///
    /// 已经在该桌面的窗口**一次调用都不发**：这个项目的窗口操作要尽可能少，
    /// 每一次都不是免费的（分层窗口的表面会因此重新合成）。
    /// </summary>
    public static int MoveAllToCurrentDesktop(IEnumerable<nint> windows)
    {
        if (!IsAvailable || Manager is null) return 0;

        var target = CurrentDesktopId();
        if (target is null) return 0;

        var moved = 0;
        foreach (var hwnd in windows)
        {
            if (hwnd == 0) continue;

            if (IsOnCurrentDesktop(hwnd))
            {
                SkippedCount++;
                continue;
            }

            if (MoveToDesktop(hwnd, target.Value)) moved++;

            // **搬完复验一次。** 搬动失败是很可能的（驱动的、策略的、窗口本身特殊），
            // 静默失败比失败更坏：用户看到的是"还是只在原来那个桌面能唤出"，而日志里什么都没有。
            // 所以这里复验并把结论写进崩溃日志，让下一次排查有据可查。
            if (!IsOnCurrentDesktop(hwnd))
            {
                FailedCount++;
                CrashLog.Write(
                    "虚拟桌面搬动失败",
                    new InvalidOperationException($"HWND 0x{hwnd:X} 搬到桌面 {target.Value} 之后仍不在当前桌面"));
            }
        }

        MovedCount += moved;
        return moved;
    }

    /// <summary>
    /// 只看不搬：某个窗口现在在不在当前桌面上。归还焦点前用它把关 ——
    /// 把焦点交给另一个桌面上的窗口，会让 Windows **把用户切过去**，
    /// 那正是"用了轮盘反而被拽回原桌面"的成因。
    ///
    /// 拿不到接口时一律当作"在"（不折腾）。
    /// </summary>
    public static bool IsOnCurrentDesktop(nint hwnd)
    {
        if (!IsAvailable || Manager is null || hwnd == 0) return true;

        try
        {
            unsafe
            {
                BOOL onCurrent;
                Manager.IsWindowOnCurrentVirtualDesktop((HWND)hwnd, &onCurrent);
                return onCurrent != default;
            }
        }
        catch
        {
            return true;
        }
    }

    private static bool MoveToDesktop(nint hwnd, Guid desktopId)
    {
        if (!IsAvailable) return false;

        try
        {
            unsafe
            {
                var id = desktopId;
                Manager!.MoveWindowToDesktop((HWND)hwnd, &id);
            }

            return true;
        }
        catch
        {
            // 个别窗口搬不动（例如被系统"固定到所有桌面"或已是特殊窗口）不必报错。
            return false;
        }
    }

    /// <summary>
    /// 当前桌面的 id，以及它是**从哪个窗口**读到的（诊断用：写进探针输出）。
    ///
    /// 取来源是一条阶梯 —— 任何一级都可能拿不到（没有前台窗口、外壳窗口拒绝查询、
    /// 提权窗口隔离…），而只要全都拿不到，搬动就会静默不发生、用户看到的还是老毛病。
    /// 所以每一级都试，最后一级一定成立（见下）。
    /// </summary>
    public static string LastIdSource { get; private set; } = "尚未取过";

    /// <summary>取当前桌面 id 时每一级的结论（诊断用：探针会打印）。</summary>
    public static IReadOnlyList<string> IdDiagnostics { get; private set; } = [];

    /// <summary>
    /// 诊断：把接口三个方法各自的原始 HRESULT 报出来。
    ///
    /// 存在的理由：本机（Win11 26200）上 `GetWindowDesktopId` 对**任何**窗口都返回
    /// `0x8002802B`（TYPE_E_ELEMENTNOTFOUND）——包括前台窗口、外壳窗口、光标下的窗口，
    /// 以及一个刚建出来并显示过的 1×1 屏幕外窗口。这与文档描述不符，所以要区分
    /// "这台机器上此路不通" 与 "我调用错了"：把三个方法的原始返回码摆出来看 ——
    /// `IsWindowOnCurrentVirtualDesktop` 是好的、且对非法 GUID 的搬动返回"元素不存在"
    /// （而不是参数非法），说明接口可达、槽位没错，断的只是"从窗口反查桌面 id"这个能力。
    /// </summary>
    public static IReadOnlyList<string> DiagnoseMethods(nint hwnd)
    {
        var lines = new List<string>();
        if (!IsAvailable || Manager is null)
        {
            lines.Add("拿不到管理器，无法诊断");
            return lines;
        }

        try
        {
            unsafe
            {
                BOOL on;
                Manager.IsWindowOnCurrentVirtualDesktop((HWND)hwnd, &on);
                lines.Add($"IsWindowOnCurrentVirtualDesktop → 成功（{(on != default ? "在当前桌面" : "不在")}）");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"IsWindowOnCurrentVirtualDesktop → 0x{ex.HResult:X8}");
        }

        try
        {
            unsafe
            {
                Guid id;
                Manager.GetWindowDesktopId((HWND)hwnd, &id);
                lines.Add($"GetWindowDesktopId → 成功 {id}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"GetWindowDesktopId → 0x{ex.HResult:X8}（{ex.GetType().Name}）");
        }

        try
        {
            unsafe
            {
                var empty = Guid.Empty;      // 明知非法：只为看它回什么码
                Manager.MoveWindowToDesktop((HWND)hwnd, &empty);
                lines.Add("MoveWindowToDesktop(空 GUID) → 竟然成功（槽位很可能错位）");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"MoveWindowToDesktop(空 GUID) → 0x{ex.HResult:X8}");
        }

        return lines;
    }

    /// <summary>
    /// 建一个**显示在屏幕外**的 1×1 窗口，读它的桌面 id，然后销毁。
    ///
    /// 为什么要"显示"：虚拟桌面管理器只跟踪它认得的窗口 —— 实测一个**只建句柄、
    /// 从不显示**的窗口去查 `GetWindowDesktopId` 会得到 `0x8002802B`
    /// （TYPE_E_ELEMENTNOTFOUND，"没有这个元素"）。而**现在**创建的窗口必定在**现在**的
    /// 桌面上，显示一下让它进入管理器的索引，就能读出来了。
    ///
    /// 屏幕外 + `SW_SHOWNOACTIVATE`：用户看不见、焦点也不动 ——
    /// 与本项目停放遮罩/轮盘用的是同一套做法（放在 -32000 那一带）。
    /// </summary>
    private static Guid? DesktopIdViaTempWindow(List<string> trace)
    {
        nint? temp = null;
        try
        {
            unsafe
            {
                const int offscreen = -30000;
                temp = PInvoke.CreateWindowEx(
                    0, "STATIC", "WinQuick.DesktopProbe", 0,
                    offscreen, offscreen, 1, 1,
                    default, default, default, null);

                if (temp is null || temp == 0)
                {
                    trace.Add("④临时窗口建不出来");
                    return null;
                }

                PInvoke.ShowWindow((HWND)temp, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            }

            var id = DesktopIdOf(temp.Value, trace, "④临时窗口(已显示)");
            trace.Add(id is null
                ? "④ 显示了也读不到 —— 这台机器上此路不通"
                : "④ 显示了就能读到");
            return id;
        }
        catch (Exception ex)
        {
            trace.Add($"④ 临时窗口异常：{ex.GetType().Name} 0x{ex.HResult:X}");
            return null;
        }
        finally
        {
            if (temp is { } hwnd && hwnd != 0)
                PInvoke.DestroyWindow((HWND)hwnd);
        }
    }

    private static Guid? CurrentDesktopId()
    {
        var trace = new List<string>();

        // 候选值按可靠程度排序，理由见下面的注释。**本机实测**：
        // GetWindowDesktopId 对任何窗口都回 0x8002802B（连刚显示出来的 1×1 屏幕外窗口也是），
        // 而 IsWindowOnCurrentVirtualDesktop 是好的 —— 所以"从窗口反查桌面"这条在这台机器上是断的，
        // 当前桌面的 GUID 只能从注册表读（PowerToys / Windows Terminal 的结论一致）。
        var candidates = new List<(string Source, Guid Id)>();

        // ① 注册表：Explorer\VirtualDesktops\CurrentVirtualDesktop（Win10/11 都有）。
        if (ReadRegistryGuid(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops") is { } current)
        {
            trace.Add($"① 注册表 VirtualDesktops → {current}");
            candidates.Add(("注册表", current));
        }
        else
        {
            trace.Add("① 注册表 VirtualDesktops 里没有 CurrentVirtualDesktop");
        }

        // ② 注册表：按会话分的那个键（较老的系统上只有它）。
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var sessionPath =
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{sessionId}\VirtualDesktops";
        if (ReadRegistryGuid(sessionPath) is { } sessionDesktop)
        {
            trace.Add($"② 注册表 SessionInfo → {sessionDesktop}");
            candidates.Add(("注册表(会话)", sessionDesktop));
        }

        // ③ 前台窗口（文档化的做法；本机取不到，别的机器上可能是好的）。
        //    **必须排除自家窗口**：覆盖层开着时轮盘就是前台窗口，而它可能正在旧桌面上。
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground != default && !IsOwnWindow((nint)foreground))
        {
            if (DesktopIdOf((nint)foreground, trace, "③前台") is { } fromForeground)
                candidates.Add(("前台窗口", fromForeground));
        }

        // ④ 光标底下的窗口（同上，也排除自家窗口 —— 轮盘可能正好在光标下）。
        if (PInvoke.GetCursorPos(out var cursor))
        {
            var under = PInvoke.WindowFromPoint(cursor);
            if (under != default && !IsOwnWindow((nint)under))
            {
                if (DesktopIdOf((nint)under, trace, "④光标处") is { } fromCursor)
                    candidates.Add(("光标处的窗口", fromCursor));
            }
        }

        // ⑤ 兜底：临时窗口。**现在**建的窗口必定在**现在**的桌面上。
        if (DesktopIdViaTempWindow(trace) is { } fromTemp)
            candidates.Add(("临时窗口", fromTemp));

        LastIdSource = candidates.Count > 0 ? candidates[0].Source : "取不到";
        IdDiagnostics = trace;
        return candidates.Count > 0 ? candidates[0].Id : null;
    }

    /// <summary>
    /// 端到端自证：用一个**一次性窗口**在桌面之间搬来搬去，验证整条机制真的成立。
    ///
    /// 为什么需要它：注册表读到的"当前桌面 GUID"是**未经验证的输入** ——
    /// 只有把它当作搬动目标、再用可靠的 <c>IsWindowOnCurrentVirtualDesktop</c> 复验，
    /// 才能证明"这个 GUID 确实是当前桌面"。做法：
    /// ① 一次性窗口搬到**另一个**桌面 → 判定应当变成"不在当前桌面"；
    /// ② 再搬回注册表给的"当前桌面" → 判定应当变回"在当前桌面"。
    /// 两次都符合预期，才说明"读注册表 → 搬窗口"这条路在这台机器上是通的。
    ///
    /// 全部用一个 1×1 的屏幕外窗口完成，用户看不见；移动窗口也不会切换用户的桌面。
    /// </summary>
    public static IReadOnlyList<string> SelfTest()
    {
        var lines = new List<string>();
        if (!IsAvailable || Manager is null)
        {
            lines.Add("拿不到管理器，无法自证");
            return lines;
        }

        var all = AllDesktopIds();
        lines.Add($"注册表里共有 {all.Count} 个桌面");

        var current = CurrentDesktopId();
        lines.Add(current is null
            ? "当前桌面 GUID：取不到"
            : $"当前桌面 GUID：{current}（来源：{LastIdSource}）");

        if (current is null || all.Count < 2)
        {
            lines.Add("桌面不足两个，跳过「搬来搬去」那两步（单桌面上本来就不需要搬）");
            return lines;
        }

        var other = all.FirstOrDefault(id => id != current.Value);
        if (other == Guid.Empty)
        {
            lines.Add("找不到另一个桌面，跳过");
            return lines;
        }

        nint? temp = null;
        try
        {
            unsafe
            {
                temp = PInvoke.CreateWindowEx(
                    0, "STATIC", "WinQuick.DesktopSelfTest", 0,
                    -30000, -30000, 1, 1, default, default, default, null);
                if (temp is null || temp == 0)
                {
                    lines.Add("一次性窗口建不出来，跳过");
                    return lines;
                }

                PInvoke.ShowWindow((HWND)temp, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            }

            var hwnd = temp.Value;
            lines.Add($"起点：IsOnCurrentDesktop = {YesNo(IsOnCurrentDesktop(hwnd))}");

            if (!MoveToDesktop(hwnd, other))
            {
                lines.Add("搬到另一个桌面：调用失败（这台机器上连搬动都不可用）");
                return lines;
            }

            var away = IsOnCurrentDesktop(hwnd);
            lines.Add($"搬到另一个桌面后：IsOnCurrentDesktop = {YesNo(away)}" +
                      (away ? "　← 不符合预期（搬了却还说在当前桌面）" : "　✓ 符合预期"));

            if (!MoveToDesktop(hwnd, current.Value))
            {
                lines.Add("搬回当前桌面：调用失败");
                return lines;
            }

            var back = IsOnCurrentDesktop(hwnd);
            lines.Add($"搬回当前桌面后：IsOnCurrentDesktop = {YesNo(back)}" +
                      (back ? "　✓ 符合预期（机制成立）" : "　← 不符合预期（注册表那个 GUID 不是当前桌面）"));
        }
        finally
        {
            if (temp is { } hwnd2 && hwnd2 != 0) PInvoke.DestroyWindow((HWND)hwnd2);
        }

        return lines;

        static string YesNo(bool value) => value ? "是" : "否";
    }

    /// <summary>注册表里的全部桌面 id（Explorer\VirtualDesktops\VirtualDesktopIDs 是 16 字节 × N）。</summary>
    private static IReadOnlyList<Guid> AllDesktopIds()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] { Length: >= 16 } blob) return [];

            var ids = new List<Guid>();
            for (var offset = 0; offset + 16 <= blob.Length; offset += 16)
                ids.Add(new Guid(blob.AsSpan(offset, 16).ToArray()));

            return ids;
        }
        catch
        {
            return [];
        }
    }

    private static Guid? ReadRegistryGuid(string subKeyPath)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKeyPath);
            return key?.GetValue("CurrentVirtualDesktop") is byte[] { Length: 16 } bytes
                ? new Guid(bytes)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 某个窗口是不是**我们自己进程**的。
    ///
    /// 取"当前桌面"时必须排除自家窗口：覆盖层正开着的时候轮盘就是前台窗口，
    /// 而它可能正留在**旧**桌面上 —— 拿它当来源会得到一个正好相反的答案。
    /// </summary>
    private static bool IsOwnWindow(nint hwnd)
    {
        try
        {
            unsafe
            {
                uint pid;
                PInvoke.GetWindowThreadProcessId((HWND)hwnd, &pid);
                return pid == (uint)Environment.ProcessId;
            }
        }
        catch
        {
            return false;
        }
    }

    private static Guid? DesktopIdOf(nint hwnd) => DesktopIdOf(hwnd, null, null);

    private static Guid? DesktopIdOf(nint hwnd, List<string>? trace, string? label)
    {
        if (!IsAvailable || Manager is null || hwnd == 0) return null;

        try
        {
            unsafe
            {
                Guid id;
                Manager.GetWindowDesktopId((HWND)hwnd, &id);
                trace?.Add($"{label} 0x{hwnd:X} → {id}");
                return id;
            }
        }
        catch (Exception ex)
        {
            trace?.Add($"{label} 0x{hwnd:X} → 失败：{ex.GetType().Name} 0x{ex.HResult:X}");
            return null;
        }
    }
}
