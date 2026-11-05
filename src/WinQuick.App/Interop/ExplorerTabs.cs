using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Interop;

/// <summary>
/// 把一批文件夹放进**同一个资源管理器窗口的多个标签页**。
///
/// ## 终选链路（2026-11-05 本机完整验证，含每个标签内容的逐个核对）
///
/// 1. <c>explorer.exe 首路径</c> 开一个新窗口 → EnumWindows 找到新 hwnd → <c>Shell.Application</c>
///    的 <c>Windows()</c> 里按 HWND 取到条目。
/// 2. 对其余每个路径：UIA 对 <c>AutomationId == "AddButton"</c> 的按钮 Invoke **建出新标签**，
///    再在新标签**自己那条** ShellWindows 条目上 <c>Navigate2</c>，最后读回该条目的
///    <c>LocationURL</c> 与目标路径比对。
///
/// 这条链不需要窗口前台、不需要标签激活、不需要注入任何输入 —— 后台完全可用。
///
/// ## 为什么是这条链：前三条路全部实测淘汰（同上日期）
///
/// 1. <c>explorer.exe "A" "B" "C"</c>：单次多参数调用**只认首个路径**，其余被忽略。
/// 2. <c>Navigate2</c> 带 <c>0x800</c>（navOpenInNewTab）/ <c>0x10000</c>（navOpenNewForegroundTab）：
///    这两个标志是给 IE 定义的，资源管理器**无视其建标签语义**，结果都只是导航当前活动标签。
/// 3. UIA Invoke「AddButton」能**创建**新标签，但新标签**不会被选中**：无论窗口是否前台
///    （SetForegroundWindow / SwitchToThisWindow / UIA <c>SetFocus</c> / 最小化再还原拿到前台之后
///    都试过）、<c>SelectionItemPattern.Select()</c> 也不生效 —— 激活只随真实用户输入发生。
///    新标签的默认内容是「此电脑」，它那条 ShellWindows 条目的 <c>LocationURL</c> 是**空串**。
///    所以「建标签 → 等激活 → 在当前活动标签上 Navigate2」的写法必然把路径落到上一个标签上，
///    新标签带着默认页留在原地。
///
/// 于是只剩第 4 条，也是决定性发现：**每个标签在 <c>Shell.Application.Windows()</c> 集合里都有
/// 自己的一条 IWebBrowser2 条目**（同一个 HWND 下多条）。对**新标签自己那条**调 Navigate2，
/// 无需任何激活即可把该标签导航到目标 —— 实测 2 标签窗口：entry#2（url 为空）Navigate2
/// 到某个文件夹之后，标签栏上第 2 个标签的名字变成该文件夹名，entry#2 的 url 也随之更新。
///
/// ## 怎么找到「新标签自己那条」条目
///
/// 点 AddButton **之前**先记下该窗口所有条目的 LocationURL（多重集，空串也算一条），
/// 点完之后再遍历，**多出来的那一条**就是新标签。不按「最后一条」取：集合的枚举顺序
/// 没有文档保证，新增条目在哪个位置都可能；只有「相对快照多出来的」是有定义的事实。
/// 万一同时多出多条（理论上不该发生），取遍历序的最后一条 —— 新标签的条目也随标签顺序追加，
/// 这是唯一能做的确定性选择。
///
/// ## 为什么不按标签名验证
///
/// 已知文件夹（下载、文档等）的**显示名是本地化的**，拿 <c>Path.GetFileName</c> 去比会误报；
/// 而 <c>LocationURL</c> 是资源管理器自己算出来的规范地址。两边都过一遍
/// <see cref="Uri.UnescapeDataString"/> 再比：explorer 给的是 <c>file:///D:/Work/...</c> 这类
/// 带 URI 转义的绝对地址，用户路径可能含空格（C:\Program Files 就是），不还原编码会永远对不上。
///
/// ## 为什么按 HWND 而不是 LocationURL 匹配 COM 窗口
///
/// 窗口的 URL 一直在变（活动标签一变就变），HWND 在整个生命周期内恒定 ——
/// 本类用 HWND 圈定「哪些条目属于我这个窗口」，再用条目自己的 URL 变化定位新标签。
///
/// ## 为什么允许阻塞
///
/// 调用方是启动队列的后台线程（见 <c>ActionExecutor.ExecuteFoldersBatch</c>），
/// 阻塞这里不会卡住界面。每一步的等待都是有意选的轮询上限（窗口不是瞬间出现的），
/// 改成异步只会让这段顺时序的 UI 操作更难读、更难保证「按钮点下去之后才导航」。
///
/// ## 为什么用计数轮询，而不是固定等待
///
/// 最初「点 AddButton → 固定 Sleep(400ms) → Navigate2」在 PowerShell 验证脚本里跑得很好，
/// 可从托盘触发启动队列时用户实测只得到 1 个标签。差别不在代码，而在**时序**：
/// 队列先启动了别的程序、explorer 被拉起时窗口就绪的节奏不同，于是 400ms 之后按钮可能还不可点，
/// Invoke 静默落空。固定值永远赌的是「这台机器够快」。现在每一步都等一个**可观测的事实**
/// （标签数 +1、条目数增加、条目 URL 变成目标），等不到就走多窗口兜底，而不是再赌更大的固定值。
///
/// ## 失败时退化成「多窗口」而不是「失败」
///
/// 每个标签都依赖那个 <c>AddButton</c>：Windows 改版把它拿掉、或 UIA 读不到时，
/// 这条链路整体不可用，继续逐个试按钮没有意义。这时从该路径起全部改为逐个 <c>Process.Start</c>，
/// 计数进 <see cref="Result.SeparateWindows"/> —— 用户得到的仍然是「文件夹都打开了」。
///
/// ## 为什么 COM 后期绑定走 dynamic 而不是 Type.InvokeMember（2026-11-05 实测教训）
///
/// 曾经的写法是 `shell.GetType().InvokeMember("Windows", ...)` 之类。这些调用在
/// <c>--tabs-probe</c> 驱动本类时直接抛 `TargetInvocationException → COMException 0x80020003
/// (DISP_E_MEMBERNOTFOUND)`，用户从托盘触发队列时崩溃日志同签名。
/// 根因：`object` 上的 `Type.InvokeMember` 走托管反射，而 COM 侧拿到的是 `System.__ComObject`，
/// 它不参与托管反射的成员查找。`dynamic` 走 C# 运行时绑定器 + IDispatch，是后期绑定该走的路。
/// **验证脚本与产品代码必须走同一条绑定路径**，否则验证通过不代表实现正确。
/// </summary>
internal static class ExplorerTabs
{
    /// <summary>打开结果。SeparateWindows&gt;0 表示有一部分退化成了独立窗口。</summary>
    public readonly record struct Result(bool Ok, string? Error, int TabsOpened, int SeparateWindows);

    private const int ExplorerWindowPollMs = 2500;
    private const int ExplorerWindowPollStepMs = 300;
    private const int ComWindowPollMs = 2000;
    private const int ComWindowPollStepMs = 100;

    // 窗口刚被 EnumWindows 看到时，UIA 树可能还没建好（按钮还读不到、标签还是 0 个）。
    // 在这两点都成立之前点按钮，Invoke 会静默落空 —— 所以先等「UI 就绪」。
    private const int UiReadyPollMs = 2000;

    // 点下 AddButton 之后，等 TabItem 计数从 count0 涨到 count0 + 1。
    // 这是「新标签真的建出来并挂上了」的确定性判据；超时即判定按钮调不动。
    private const int NewTabPollMs = 1500;

    // 标签挂上之后，等 ShellWindows 里多出那条属于新标签的条目（新标签默认「此电脑」，
    // 条目 URL 为空串，所以只能靠「相对快照多出来」识别，见类注释）。
    private const int NewEntryPollMs = 1500;

    // 对新标签的条目 Navigate2 之后，等它自己的 LocationURL 变成目标路径。
    // 这是整条链唯一的「结果真的落对了」判据；超时即按失败处理。
    private const int EntryUrlPollMs = 2000;

    private const int PollStepMs = 100;

    // 两个标签之间的余量：连续 Invoke 太快时资源管理器会丢一次按钮响应。
    private const int BetweenTabsMs = 250;

    /// <param name="strictPreflight">
    /// 探针专用：目标窗口承载不了导航时**直接失败**，而不是退回多窗口。
    /// 产品要的是「文件夹都打开」，静默兜底是对的；但探针要抓住的就是「那一次兜底」本身 ——
    /// 兜底后文件夹都开了，分不清是哪个环节出的问题。
    /// </param>
    public static Result OpenAll(IReadOnlyList<string> folders, bool strictPreflight = false)
    {
        if (folders is null || folders.Count == 0)
            return new Result(false, "没有要打开的文件夹", 0, 0);

        if (folders.Count == 1)
        {
            Process.Start(new ProcessStartInfo { FileName = folders[0], UseShellExecute = true });
            return new Result(true, null, 1, 0);
        }

        var before = EnumerateExplorerWindows();

        // 只传首个路径：多参数实测只认第一个，拼上其余参数没有意义（见类注释）。
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = Quote(folders[0]),
            UseShellExecute = true,
        });

        var hwnd = WaitForNewExplorerWindow(before);
        if (hwnd == 0)
            return SeparateWindows(folders, 1);

        // COM 对象一律用 dynamic：Type.InvokeMember 在 System.__ComObject 上走托管反射，
        // 查不到 IDispatch 成员（0x80020003），见类注释。
        dynamic? shell = null;
        dynamic? windows = null;

        // 条目在 finally 里统一释放，所以在 try 之外持有；每项是独立 RCW，
        // 连同 windows/shell 一起释放才算不把 explorer 挂在我们的引用上。
        var entries = new List<dynamic>();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            if (shell is null)
                return SeparateWindows(folders, 1);

            windows = shell.Windows();

            foreach (var entry in EntriesForHwnd(windows, hwnd))
                entries.Add(entry);

            if (entries.Count == 0)
                return SeparateWindows(folders, 1);

            // 新窗口刚被 EnumWindows 看到时 UI 树往往还没建好：先等到「按钮读得到、且至少有一个标签」。
            if (!WaitUiReady(hwnd))
                return CloseAndFallback(folders, hwnd);

            // 首个标签由 explorer.exe 的路径参数打开，通常已经在目标上；但冷启动时可能还在跳转。
            // 只有拿到「它确实在首个目标上」这条事实，才能把它当作后续识别的基线（见下）；
            // 拿不到就只留日志 —— 首个标签也不该默认被当成目标路径，否则「此电脑」会被一并接纳。
            if (!WaitEntryUrl(entries[0], folders[0]))
            {
                CrashLog.Write("首个标签未落在目标路径",
                    new InvalidOperationException($"首个标签的 LocationURL 与目标 {folders[0]} 不一致"));
            }

            var opened = 1;
            var failedAt = 0;

            for (var i = 1; i < folders.Count; i++)
            {
                var count0 = TabCount(hwnd);

                // 每次迭代重新 FindFirst：窗口刚建好时 UIA 树还在变，
                // 复用上一轮的 pattern 对象可能指向已经被重建的元素。
                var button = FindAddTabButton(hwnd);

                // 按钮读不到 / 点不动：从这一项起全部退化，不再尝试 UIA（见类注释）。
                if (button is null || !TryInvoke(button))
                {
                    failedAt = i;
                    break;
                }

                // 确定性判据：新标签真的建出来并挂上了才算这一步成功。
                if (!WaitTabCount(hwnd, count0 + 1))
                {
                    // 稳定失败时要留得下证据：按钮在不在、能不能读模式、标签数到底是多少。
                    // 没有这些，只能看到「没涨到 N」，分不清是点不动还是建了又没了。
                    CrashLog.Write("新建标签失败",
                        new InvalidOperationException($"第 {i + 1} 个文件夹：期望标签数 {count0 + 1}，实测 {TabCount(hwnd)}；"
                            + $"AddButton {(button is null ? "读不到" : "读得到")}"));

                    if (strictPreflight)
                        return CloseAndFail(folders, hwnd, $"第 {i + 1} 个文件夹：AddButton 点下后标签数没有涨到 {count0 + 1}");

                    failedAt = i;
                    break;
                }

                // 见到第 i+1 个标签，才证明新标签的条目是相对「此前的全部条目」多出来的。
                // 首个标签没按时到位时，基线里可能混着一条「此电脑」（URL 为空）——
                // 它的 URL 正好与新标签默认页相同，不先认到它，识别新条目时就会对称地漏掉。
                foreach (var stale in EntriesForHwnd(windows, hwnd))
                {
                    var known = false;
                    foreach (var e in entries)
                    {
                        if (ReferenceEquals(e, stale)) { known = true; break; }
                    }

                    if (!known) entries.Add(stale);
                }

                // 新标签的条目（它不需要也不会有激活状态，见类注释）。
                // 基线与候选都取「补全后的 entries」，并且只拿前 count0 条（= 新标签之前的标签）
                // 当基线、从第 count0 条起找候选：同一份列表分两段比，不会出现
                // 「计数器读到的条目比实际少一条、于是把新标签自己抵消掉」这种对称抵消。
                var entry = FindNewEntry(entries, earlierCount: count0);
                if (entry is null)
                {
                    if (strictPreflight)
                        return CloseAndFail(folders, hwnd, $"第 {i + 1} 个文件夹：ShellWindows 里没有多出属于新标签的条目");

                    failedAt = i;
                    break;
                }

                // 列表里可能没有它（取值时列表又变了）：补进去，让它随 finally 一起被释放。
                var found = false;
                foreach (var e in entries)
                {
                    if (ReferenceEquals(e, entry)) { found = true; break; }
                }
                if (!found)
                    entries.Add(entry);

                Navigate(entry, folders[i]);

                // 整条链唯一的「结果真的落对了」判据：新标签**自己的**条目 URL 变成目标。
                // 不用 TabItem 名称（本地化显示名会误报，见类注释）。
                if (!WaitEntryUrl(entry, folders[i]))
                {
                    if (strictPreflight)
                        return CloseAndFail(folders, hwnd, $"第 {i + 1} 个文件夹：新标签条目的 LocationURL 在超时内没有变成目标路径");

                    failedAt = i;
                    break;
                }

                opened++;

                // 导航目标是新标签自己那条，已在、也已导航成功（上一步判据保证了），
                // 这里的余量只是让 explorer 别把连续两个操作挤丢。
                Thread.Sleep(BetweenTabsMs);
            }

            if (failedAt > 0)
            {
                var rest = SeparateWindows(folders, failedAt);
                return new Result(true, null, opened, rest.SeparateWindows);
            }

            // 终检：不判失败（文件夹都打开了），但落一条崩溃日志 ——
            // 「队列只开出一个标签」这类问题就是从这个数对不上开始的，日志里要能看见它被触发。
            var final = TabCount(hwnd);
            if (final != folders.Count)
                CrashLog.Write("标签页终检不符",
                    new InvalidOperationException($"期望 {folders.Count} 个标签，实测 {final} 个"));

            return new Result(true, null, opened, 0);
        }
        finally
        {
            // 释放失败不影响结果：COM 对象最终由 GC 回收，这里只为不把 explorer 挂住。
            // 条目先于 windows/shell 释放，且用 FinalReleaseComObject：逐项 try/catch，
            // 某一项已经断开也不影响其余对象被释放。
            foreach (var entry in entries)
            {
                try
                {
                    Marshal.FinalReleaseComObject((object)entry);
                }
                catch
                {
                    // 已断开或已被回收：跳过。
                }
            }

            Release(windows);
            Release(shell);
        }
    }

    /// <summary>
    /// 探针的失败出口：不把剩下的文件夹开出去，而是先把**我们刚建出来的**窗口关掉。
    /// 探针进程的输出在退出时还要被读到，让一批点错了文件的窗口留在桌面上没有意义；
    /// 关的是自己新建的，用户原本的窗口一律不碰。
    /// </summary>
    private static Result CloseAndFail(IReadOnlyList<string> folders, nint hwnd, string error)
    {
        PostClose(hwnd);
        return new Result(false, error, 1, 0);
    }

    /// <summary>产品路径上的「UI 就绪失败」出口：关掉半成品窗口，再从首个文件夹起逐个开独立窗口。</summary>
    private static Result CloseAndFallback(IReadOnlyList<string> folders, nint hwnd)
    {
        // 关不掉就留在那里：宁可多一个空窗口，也不要把文件夹丢掉。
        PostClose(hwnd);
        return SeparateWindows(folders, 1);
    }

    private static void PostClose(nint hwnd)
    {
        try
        {
            if (PInvoke.IsWindow((HWND)hwnd))
                PInvoke.PostMessage((HWND)hwnd, PInvoke.WM_CLOSE, default, default);
        }
        catch
        {
            // 清理失败不影响结论。
        }
    }

    /// <summary>兜底：从 <paramref name="startIndex"/> 起逐个开独立窗口。前 <paramref name="startIndex"/> 个视为已开。</summary>
    private static Result SeparateWindows(IReadOnlyList<string> folders, int startIndex)
    {
        for (var i = startIndex; i < folders.Count; i++)
        {
            // 单项失败不中止整批：其余的文件夹仍然应该打开。
            try
            {
                Process.Start(new ProcessStartInfo { FileName = folders[i], UseShellExecute = true });
            }
            catch
            {
                // 忽略：兜底路径再抛出去只会丢掉后面本来能开的项。
            }
        }

        return new Result(true, null, startIndex, folders.Count - startIndex);
    }

    /// <summary>
    /// 在 <paramref name="windows"/> 里取 HWND 匹配的**全部**条目。
    /// 返回多条是常态而不是异常：同一个 HWND 在 ShellWindows 里每个标签各有一条 IWebBrowser2 条目
    /// （2026-11-05 实测），这正是本类能后台导航的依据 —— 新标签有自己的条目，不必先激活它。
    /// 参数与返回值都是 dynamic：反射在 System.__ComObject 上查不到 IDispatch 成员，见类注释。
    /// </summary>
    private static List<dynamic> EntriesForHwnd(dynamic windows, nint hwnd)
    {
        var result = new List<dynamic>();
        if (windows is null) return result;

        var deadline = Environment.TickCount64 + ComWindowPollMs;
        while (true)
        {
            foreach (dynamic w in windows)
            {
                try
                {
                    // HWND 是 long（显式转 long 是为了让绑定器走属性读取，
                    // 而不是在 LateBoundCall 上做 object→nint 的隐式转换）。
                    if ((long)w.HWND == hwnd)
                        result.Add(w);
                }
                catch
                {
                    // 窗口正在销毁时读属性会抛，跳过它继续找。
                }
            }

            // 等到至少一条就返回：首条对应首个标签，循环里会再取一次把后续标签的条目补齐。
            if (result.Count > 0 || Environment.TickCount64 >= deadline) return result;
            Thread.Sleep(ComWindowPollStepMs);
        }
    }

    /// <summary>
    /// 当前条目的 LocationURL 多重集（空串也入集）。用作「新标签的条目」的识别基线：
    /// 新标签默认停在「此电脑」，它的条目 URL 是空串 —— 空串不入集就识别不出它。
    /// </summary>
    private static List<string> EntryUrls(IReadOnlyList<dynamic> entries)
    {
        var urls = new List<string>(entries.Count);
        foreach (var entry in entries)
            urls.Add(TryGetLocationUrl(entry) ?? "");
        return urls;
    }

    /// <summary>
    /// 在 <paramref name="entries"/> 里找「新标签自己那条」：前 <paramref name="earlierCount"/> 条
    /// （= 已存在的标签）当基线，从其后找「URL 不在基线多重集里」的那条。
    /// 按多重集差集而不是「最后一条」：集合的枚举顺序没有文档保证；而「相对已有标签多出来的
    /// URL」是有定义的事实。同 URL 的条目按个数抵消 —— 同名文件夹开两次时，多出来的那条才是新的。
    /// 找不到返回 null，由调用方兜底。
    /// </summary>
    private static dynamic? FindNewEntry(IReadOnlyList<dynamic> entries, int earlierCount)
    {
        var deadline = Environment.TickCount64 + NewEntryPollMs;
        while (true)
        {
            var baseline = new List<string>(earlierCount);
            for (var k = 0; k < earlierCount && k < entries.Count; k++)
                baseline.Add(TryGetLocationUrl(entries[k]) ?? "");

            dynamic? lastExtra = null;
            for (var k = earlierCount; k < entries.Count; k++)
            {
                var url = TryGetLocationUrl(entries[k]) ?? "";
                var index = baseline.IndexOf(url);
                if (index >= 0)
                {
                    baseline.RemoveAt(index);
                    continue;
                }

                lastExtra = entries[k];
            }

            if (lastExtra is not null) return lastExtra;

            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(PollStepMs);
        }
    }

    /// <summary>
    /// 返回 URL 与 <paramref name="path"/> 一致的第一条；都没有返回 null。
    /// 顺序无保证时，按「内容」而不是「位置」认条目是唯一的确定性做法。
    /// </summary>
    private static dynamic? FindEntryByUrl(IReadOnlyList<dynamic> entries, string path)
    {
        foreach (var entry in entries)
        {
            if (UrlMatches(TryGetLocationUrl(entry), path))
                return entry;
        }

        // 新窗口刚出来时条目 URL 可能还没填上，等一小会儿再认一次。
        var deadline = Environment.TickCount64 + ComWindowPollMs;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(ComWindowPollStepMs);
            foreach (var entry in entries)
            {
                if (UrlMatches(TryGetLocationUrl(entry), path))
                    return entry;
            }
        }

        return null;
    }

    private static string? TryGetLocationUrl(dynamic entry)
    {
        try
        {
            return (string)entry.LocationURL;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>等到该条目自己的 LocationURL 与 <paramref name="path"/> 一致，超时返回 false。</summary>
    private static bool WaitEntryUrl(dynamic entry, string path)
    {
        var deadline = Environment.TickCount64 + EntryUrlPollMs;
        while (true)
        {
            if (UrlMatches(TryGetLocationUrl(entry), path)) return true;

            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(PollStepMs);
        }
    }

    private const string FileUrlPrefix = "file:///";

    /// <summary>
    /// 比较资源的规范地址与本地路径。explorer 给的是 <c>file:///D:/Work/...</c> 这样的绝对 URI，
    /// 空格等字符是百分号转义的，所以先还原编码、再剥掉 <c>file:///</c> 前缀再比；
    /// 反斜杠与正斜杠视为等价（OrdinalIgnoreCase）。
    ///
    /// 为什么要剥前缀（2026-11-05 实测教训）：<c>LocationURL</c> 是**带 scheme 的绝对 URI**，
    /// 只做 <see cref="Uri.UnescapeDataString"/> 得到的是 <c>file:///C:/Program Files</c>，
    /// 拿它和裸路径 <c>C:\Program Files</c> 直接比较**恒为假** —— 于是导航明明已经成功，
    /// 校验却永远超时，还被兜底解释成「新标签没导航过去」。比较口径要与被比较的对象一致。
    /// </summary>
    internal static bool UrlMatches(string? locationUrl, string path)
    {
        if (string.IsNullOrEmpty(locationUrl)) return false;

        var trimmed = locationUrl!.Trim().TrimEnd('/');
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(trimmed);
        }
        catch
        {
            decoded = trimmed;
        }

        if (decoded.StartsWith(FileUrlPrefix, StringComparison.OrdinalIgnoreCase))
            decoded = decoded[FileUrlPrefix.Length..];
        else if (decoded.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // file:C:/… / file://host/… 这类不常见的写法：交给 Uri 归一成路径，归不出来就按原串比。
            decoded = Uri.TryCreate(decoded, UriKind.Absolute, out var uri) && uri.IsFile
                ? uri.LocalPath
                : decoded;
        }

        var normalizedPath = path.Replace('/', '\\').TrimEnd('\\');
        if (string.Equals(decoded.Replace('/', '\\'), normalizedPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(decoded, path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 找到工具栏上的「添加新标签页」按钮，按 AutomationId 匹配所以与界面语言无关。
    /// 返回**元素本身**而不是 InvokePattern：Pattern 对象在按钮被 explorer 重建时会失效，
    /// 而调用点紧跟在查找之后，拿着元素去 Invoke 少一层可能过期的间接引用。
    /// </summary>
    private static AutomationElement? FindAddTabButton(nint hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var condition = new PropertyCondition(
                AutomationElement.AutomationIdProperty, "AddButton");
            return root.FindFirst(TreeScope.Descendants, condition);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>等到「AddButton 读得到、且至少有一个 TabItem」，超时返回 false。</summary>
    private static bool WaitUiReady(nint hwnd)
    {
        var deadline = Environment.TickCount64 + UiReadyPollMs;
        while (true)
        {
            if (FindAddTabButton(hwnd) is not null && TabCount(hwnd) >= 1) return true;

            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(PollStepMs);
        }
    }

    /// <summary>等到标签数恰好等于 <paramref name="target"/>，超时返回 false。</summary>
    private static bool WaitTabCount(nint hwnd, int target)
    {
        var deadline = Environment.TickCount64 + NewTabPollMs;
        while (true)
        {
            if (TabCount(hwnd) == target) return true;

            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(PollStepMs);
        }
    }

    /// <summary>
    /// 数一个窗口里 UIA 的 TabItem 数量；数不出来返回 -1（窗口正在重建、UIA 调用失败等）。
    /// 用计数而不是「按钮点没点下去」来判断，是因为 Invoke 返回成功并不等于标签建出来了。
    /// </summary>
    private static int TabCount(nint hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return -1;

            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            return root.FindAll(TreeScope.Descendants, condition).Count;
        }
        catch
        {
            return -1;
        }
    }

    private static bool TryInvoke(AutomationElement button)
    {
        try
        {
            if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                return false;

            ((InvokePattern)pattern).Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在**该标签自己的条目**上调用 Navigate2 —— 不需要这个标签是活动标签（见类注释）。
    /// 不带 0x800 / 0x10000 标志：那组标志对资源管理器无效，而且我们本来就不想再建标签。
    /// </summary>
    private static void Navigate(dynamic entry, string path)
    {
        try
        {
            entry.Navigate2(path);
        }
        catch
        {
            // 导航失败由随后的 URL 校验兜住并进入兜底，不让它把整批变成失败。
        }
    }

    private static nint WaitForNewExplorerWindow(HashSet<nint> before)
    {
        var deadline = Environment.TickCount64 + ExplorerWindowPollMs;
        while (true)
        {
            foreach (var hwnd in EnumerateExplorerWindows())
            {
                if (!before.Contains(hwnd)) return hwnd;
            }

            if (Environment.TickCount64 >= deadline) return 0;
            Thread.Sleep(ExplorerWindowPollStepMs);
        }
    }

    /// <summary>当前所有可见的资源管理器顶层窗口（CabinetWClass）。</summary>
    private static HashSet<nint> EnumerateExplorerWindows()
    {
        var result = new HashSet<nint>();
        // 委托变量保活：EnumWindows 在调用期间同步回调，丢了引用会被 GC 而崩溃。
        WNDENUMPROC callback = (hwnd, _) =>
        {
            if (!PInvoke.IsWindowVisible(hwnd)) return true;

            Span<char> buffer = stackalloc char[64];
            var length = PInvoke.GetClassName(hwnd, buffer);
            if (length > 0 && buffer[..length].SequenceEqual("CabinetWClass"))
                result.Add((nint)hwnd);

            return true;
        };

        PInvoke.EnumWindows(callback, default);
        GC.KeepAlive(callback);
        return result;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static void Release(object? com)
    {
        if (com is null) return;
        try
        {
            Marshal.ReleaseComObject(com);
        }
        catch
        {
            // 见 finally 处的说明。
        }
    }
}
