using System.IO;
using System.Windows.Automation;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.M0;

/// <summary>
/// 多标签打开探针：直接调用产品代码 <see cref="ExplorerTabs.OpenAll"/>，在本机复现
/// 「启动队列只开出一个标签」的问题，并作为 <see cref="ExplorerTabs"/> 时序判据的回归锁。
///
/// 为什么要探针而不是继续在 PowerShell 里验：那段验证脚本用的是自己写的 MTA/STA 链路，
/// 它通过只能说明「那条链路本身写得对」，说明不了产品代码在真实队列时序下会怎样。
/// 这里调的是**同一个方法**（经 InternalsVisibleTo 可见的 internal），
/// 所以这里通过 = 产品这条路径通过。
///
/// 判据全部是**能失败**的对照检查：
///   a) 只新增了一个资源管理器窗口（标签打开成功时不该多出窗口）；
///   b) 新窗口里的 TabItem 数等于参数个数；
///   c) 所有窗口的 TabItem 总数增量等于参数个数，且与返回的 TabsOpened 自洽；
///   d) **标签内容**：新窗口里第 i 个 TabItem 的 Name（UIA 的 TabItem 名就是标签显示的文件夹名）
///      等于第 i 个文件夹的显示名；e) 没有任何标签的 Name 是「主页」「此电脑」。
///
/// d/e 是 2026-11 补上的 —— 只有 b/c 时探针是绿的，而用户实测「多个文件夹合并开标签，
/// 只有第一个正确，后面全是默认页」。数量只证明标签挂上了，证明不了 Navigate2 落对了标签。
///
/// 输入路径要用**不本地化**的目录名（C:\Windows、C:\Program Files、D:\Work\WinQuick）：
/// 显示名是本地化的（如「下载」），拿 Path.GetFileName 去比会误报。所以探针**自检输入**：
/// 传了已知本地化目录（Downloads/Documents/Desktop 的英文与中文名）只打印提醒、继续跑；
/// f) 另有独立断言 —— 新窗口里没有任何标签叫「此电脑」或「主页」，这条不依赖显示名是否本地化。
///
/// 产品侧 2026-11-05 改成「对新标签自己的 ShellWindows 条目 Navigate2」，探针无需跟着改判据：
/// 它核对的是**结果**（标签数、标签名、总数增量），链路怎么走不影响这些断言能否失败。
///
/// 会动真实资源管理器：只关**本次新开**的窗口（基线之外的 hwnd），用户原本开着的窗口一律不碰。
/// </summary>
public static class TabsProbe
{
    private const string Usage = """
        用法: WinQuick.Probes.exe --tabs-probe <文件夹1> <文件夹2> [<文件夹3>...]

        至少两个文件夹。每个路径必须已存在（不存在的路径会被资源管理器换成「找不到」窗口，
        那样数出来的标签与参数对不上，属于探针输入错误而不是产品缺陷）。
        """;

    /// <summary>OpenAll 返回后等 explorer 把标签与导航都落定，再做对照检查。</summary>
    private const int SettleMs = 1200;

    /// <summary>数 TabItem 的重试上限：关窗口前的清理检查用它，避免把正在消失的窗口算进去。</summary>
    private const int TabCountPollMs = 1500;

    public static void Run(CliOptions cli)
    {
        ProbeOutput.Initialize("tabs", "m4");

        var results = new List<(string Name, bool Passed, string Detail)>();
        var folders = cli.Positionals;

        ProbeOutput.Section("输入");
        ProbeOutput.Kv("文件夹个数", folders.Count.ToString());

        var inputProblem = ValidateInput(folders);
        if (inputProblem is not null)
        {
            ProbeOutput.Line("  " + inputProblem);
            ProbeOutput.Line();
            ProbeOutput.Line(Usage);

            // 输入不合法要判 FAIL：否则一次参数写错的运行会得到「全部通过」，比没跑更糟。
            results.Add(("输入合法", false, inputProblem));
            Report(results);
            return;
        }

        foreach (var folder in folders)
            ProbeOutput.Line("  " + folder);

        // 不拦截、只提醒：本地化显示名会让下面「Name == 文件名」那条假失败，
        // 但探针已经能跑（其余断言照样有效），直接 FAIL 会把一次参数味道不对的运行变成红灯噪音。
        var localized = folders.Where(IsLocalizedFolder).ToList();
        if (localized.Count > 0)
        {
            ProbeOutput.Line("  提醒：以下目录在资源管理器里显示为本地化名称（如「下载」），"
                + "「Name == 文件名」那条可能误报，建议换 C:\\Windows、C:\\Program Files、D:\\Work\\WinQuick：");
            foreach (var folder in localized)
                ProbeOutput.Line("    " + folder);
        }
        if (folders.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != folders.Count)
        {
            ProbeOutput.Line("  提醒：有多个参数的文件名相同，「Name == 文件名」无法区分它们，建议换成不同名字的目录。");
        }

        // 基线：本次运行之前就存在的窗口与它们各自的标签数。
        // 清理只认这份基线之外的新 hwnd —— 用户开着的窗口一个都不动。
        var before = EnumerateExplorerWindows();
        var beforeCounts = before.ToDictionary(h => h, TabCount);
        var beforeTabs = beforeCounts.Values.Where(c => c >= 0).Sum();

        ProbeOutput.Section("基线（探针启动前已开着的资源管理器窗口）");
        ProbeOutput.Kv("窗口数", before.Count.ToString());
        foreach (var (hwnd, count) in beforeCounts)
            ProbeOutput.Kv($"  0x{hwnd:X}", (count < 0 ? "数不出来" : count.ToString()) + " 个标签");
        ProbeOutput.Kv("TabItem 总数", beforeTabs.ToString());

        ProbeOutput.Section("调用产品代码 ExplorerTabs.OpenAll");

        // 探针进程本身有消息循环（ProbeHost 起了 WPF Application），这里同步阻塞，
        // 与产品里启动队列后台线程的调用形态一致：UI 线程不参与这段等待。
        ExplorerTabs.Result result;
        try
        {
            // StrictPreflight 让产品在「目标窗口承载不了导航」时硬失败，而不是悄悄兜底 ——
            // 不带它时这类问题会表现为「文件夹都打开了」，探针只能从标签名侧面推断。
            result = ExplorerTabs.OpenAll(folders, strictPreflight: true);
        }
        catch (Exception ex)
        {
            ProbeOutput.Kv("调用抛异常", ex.GetType().Name + ": " + ex.Message);
            results.Add(("OpenAll 不抛异常", false, ex.Message));
            CloseNewWindows(before);
            Report(results);
            return;
        }

        ProbeOutput.Kv("Ok", result.Ok);
        ProbeOutput.Kv("Error", result.Error ?? "(无)");
        ProbeOutput.Kv("TabsOpened", result.TabsOpened);
        ProbeOutput.Kv("SeparateWindows", result.SeparateWindows);

        results.Add(("OpenAll 返回 Ok", result.Ok, result.Error ?? ""));

        // 让 explorer 把标签与导航都落定，再看结果。
        Thread.Sleep(SettleMs);

        var after = EnumerateExplorerWindows();
        var newWindows = after.Where(h => !before.Contains(h)).ToList();
        var counts = after.ToDictionary(h => h, TabCount);
        var afterTabs = counts.Values.Where(c => c >= 0).Sum();

        ProbeOutput.Section("对照检查");
        ProbeOutput.Kv("新增窗口数", newWindows.Count.ToString());
        foreach (var hwnd in newWindows)
            ProbeOutput.Kv($"  0x{hwnd:X}", (counts[hwnd] < 0 ? "数不出来" : counts[hwnd].ToString()) + " 个标签");
        ProbeOutput.Kv("TabItem 总数", $"{beforeTabs} → {afterTabs}（增量 {afterTabs - beforeTabs}）");

        // a) 标签全部合进一个新窗口时，新增窗口应当恰好是 1 个（含兜底时每个文件夹各一个）。
        var expectedNewWindows = result.SeparateWindows == 0 ? 1 : 1 + result.SeparateWindows;
        results.Add(("新增窗口数与 SeparateWindows 相符",
            newWindows.Count == expectedNewWindows,
            $"新增 {newWindows.Count}，期望 {expectedNewWindows}（SeparateWindows={result.SeparateWindows}）"));

        // b) 走标签路径时，新窗口里的 TabItem 数必须等于参数个数 —— 这正是「只开出一个标签」会失败的那条。
        if (result.SeparateWindows == 0)
        {
            var newTabs = newWindows.Count == 1 ? counts[newWindows[0]] : -1;
            results.Add(("新窗口的标签数 == 参数个数",
                newTabs == folders.Count,
                $"实测 {newTabs}，期望 {folders.Count}"));
        }
        else
        {
            ProbeOutput.Line("  （在兜底路径上，跳过「新窗口标签数」这一条：入口那个窗口只承载了首个文件夹）");
        }

        // d/e) 标签内容：按顺序比 Name，并确认没有标签停在默认页。
        // 只在标签路径且恰好一个新窗口时判 —— 兜底路径下每个文件夹各占一个窗口，顺序与合并无关。
        if (result.SeparateWindows == 0 && newWindows.Count == 1)
        {
            // 逐个标签打上 IsSelected：失败时要能分清「导航落错了标签」与「哪个标签才是活动标签」，
            // 否则只能看到一个名字列表，还得再跑一次才猜得出原因（第一次修这个问题时就吃了一次）。
            var names = TabNames(newWindows[0]);
            var selected = TabSelectedFlags(newWindows[0]);
            var tagged = names.Select((n, k) => n + (k < selected.Count && selected[k] ? "（活动）" : ""));
            ProbeOutput.Kv("新窗口标签名", "「" + string.Join("」「", tagged) + "」");
            ProbeOutput.Kv("新窗口活动窗口地址（COM LocationURL）", ComLocationUrl(newWindows[0]) ?? "(读不到)");

            var expected = folders.Select(Path.GetFileName).ToList();
            var contentOk = names.Count == expected.Count
                && names.Zip(expected).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));

            // 失败时逐下标点名：一次就看清是第几个标签错、以及它的活动状态 ——
            // 只给一整串名字，还得再跑一次才知道错在哪一个。
            if (!contentOk && names.Count == expected.Count)
            {
                for (var k = 0; k < expected.Count; k++)
                {
                    var same = string.Equals(names[k], expected[k], StringComparison.Ordinal);
                    ProbeOutput.Kv($"  下标 {k}", $"{(same ? "OK" : "不符")} 实测「{names[k]}」期望「{expected[k]}」"
                        + (k < selected.Count && selected[k] ? "（活动）" : ""));
                }
            }

            results.Add(("新窗口每个标签的 Name == 对应文件夹显示名",
                contentOk,
                $"实测「{string.Join("」「", names)}」，期望「{string.Join("」「", expected)}」"));

            // 「主页」「此电脑」出现即说明那次导航没落在新标签上，而是留在了默认页 ——
            // 这正是用户看到的现象，所以它单独成一条，失败时一眼能对上用户的话。
            // 两种默认页名字都要认：旧版是「主页」，Win11 是「此电脑」（英文版分别为 Home / This PC）。
            var defaults = names.Where(n => n is "主页" or "此电脑" or "Home" or "This PC").ToList();
            results.Add(("没有标签停在默认页（主页/此电脑）",
                defaults.Count == 0,
                defaults.Count == 0 ? "" : $"出现了 {defaults.Count} 个：{string.Join("、", defaults)}"));
        }
        else
        {
            ProbeOutput.Line("  （不在「标签路径 + 恰好一个新窗口」上，跳过标签内容断言）");
        }

        // c) 总数增量：标签路径下应等于参数个数；兜底路径下每个独立窗口各 +1，合计仍是参数个数。
        results.Add(("TabItem 总数增量 == 参数个数",
            afterTabs - beforeTabs == folders.Count,
            $"增量 {afterTabs - beforeTabs}，期望 {folders.Count}"));

        results.Add(("TabsOpened + SeparateWindows == 参数个数",
            result.TabsOpened + result.SeparateWindows == folders.Count,
            $"{result.TabsOpened} + {result.SeparateWindows} vs {folders.Count}"));

        CloseNewWindows(before);
        Report(results);
    }

    /// <summary>
    /// 清理：只关本次新开的窗口（基线之外的 hwnd）。
    /// 先 WM_CLOSE 等一会儿，还没消失的再关一次 —— 实测一次 PostMessage 常被 explorer 吞掉，
    /// 残留窗口会让下一轮探针的基线里混进上一次的新窗口，读数就乱了。
    /// </summary>
    private static void CloseNewWindows(HashSet<nint> before)
    {
        var newWindows = EnumerateExplorerWindows().Where(h => !before.Contains(h)).ToList();

        ProbeOutput.Section("清理（只关本次新开的窗口）");
        if (newWindows.Count == 0)
        {
            ProbeOutput.Kv("新增窗口", "无");
            return;
        }

        foreach (var hwnd in newWindows)
            ProbeOutput.Kv($"  0x{hwnd:X}", PostClose(hwnd));

        if (WaitWindowsGone(newWindows, TabCountPollMs))
        {
            ProbeOutput.Kv("新开窗口已全部关闭", "是");
        }
        else
        {
            var left = newWindows.Where(h => PInvoke.IsWindow((HWND)h)).ToList();
            foreach (var hwnd in left)
                ProbeOutput.Kv($"  0x{hwnd:X}", "重试 · " + PostClose(hwnd));

            var closed = WaitWindowsGone(left, TabCountPollMs);
            ProbeOutput.Kv("新开窗口已全部关闭", closed ? "是（第二次 WM_CLOSE 生效）" : "否（可能残留，见上方 hwnd）");
        }

        ProbeOutput.Line("  （基线里的窗口一律未动）");
    }

    private static string PostClose(nint hwnd)
    {
        if (!PInvoke.IsWindow((HWND)hwnd)) return "已经不存在";

        // 用 WM_CLOSE 而不是 TerminateProcess：资源管理器是共享进程，杀进程会连带关掉用户的窗口。
        // WM_CLOSE 走的是「用户点了关闭」同一条路径（有未关闭标签时会提示，这里由用户处理）。
        return PInvoke.PostMessage((HWND)hwnd, PInvoke.WM_CLOSE, default, default)
            ? "已投递 WM_CLOSE"
            : "投递失败";
    }

    /// <summary>
    /// 参数是否是资源管理器会显示成**本地化名**的已知用户目录。
    /// 只提醒不拦截：探针的核心判据（数量、总数增量、无默认页）与显示名无关，照样有效。
    /// </summary>
    private static bool IsLocalizedFolder(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        string[] known =
        [
            "Downloads", "Documents", "Desktop", "Pictures", "Music", "Videos", "OneDrive",
            "下载", "文档", "桌面", "图片", "音乐", "视频",
        ];

        if (known.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 「桌面」只出现在「用户\用户名\桌面」这一层；再按路径形状认一次，覆盖英文版系统上的同义目录。
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(userProfile)
            && path.TrimEnd('\\', '/').StartsWith(userProfile, StringComparison.OrdinalIgnoreCase)
            && known.Any(k => path.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ValidateInput(IReadOnlyList<string> folders)
    {
        if (folders.Count < 2)
            return $"至少需要 2 个文件夹，实际 {folders.Count} 个";

        var missing = folders.Where(f => !Directory.Exists(f)).ToList();
        if (missing.Count > 0)
            return "以下路径不存在或不是文件夹：" + string.Join("、", missing);

        return null;
    }

    private static bool WaitWindowsGone(IReadOnlyList<nint> hwnds, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (hwnds.All(h => !PInvoke.IsWindow((HWND)h)))
                return true;

            if (Environment.TickCount64 >= deadline)
                return false;

            Thread.Sleep(100);
        }
    }

    /// <summary>可见的 CabinetWClass 顶层窗口。</summary>
    private static HashSet<nint> EnumerateExplorerWindows()
    {
        var result = new HashSet<nint>();

        // 委托保活：EnumWindows 在调用期间同步回调，丢了引用会被 GC 而崩溃。
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

    /// <summary>与产品里的判据同一种数法（ControlType.TabItem 的 Descendants 数量）；数不出来返回 -1。</summary>
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

    /// <summary>按 UIA 顺序取 TabItem 的 Name；读不到返回空表（让断言自然失败，而不是跳过）。</summary>
    private static List<string> TabNames(nint hwnd)
    {
        var names = new List<string>();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return names;

            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            foreach (AutomationElement tab in root.FindAll(TreeScope.Descendants, condition))
                names.Add(tab.Current.Name ?? "");
        }
        catch
        {
            names.Clear();
        }

        return names;
    }

    /// <summary>每个 TabItem 的 IsSelected；读不到模式的位置记 false。</summary>
    private static List<bool> TabSelectedFlags(nint hwnd)
    {
        var flags = new List<bool>();
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return flags;

            var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
            foreach (AutomationElement tab in root.FindAll(TreeScope.Descendants, condition))
            {
                var ok = tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                    && pattern is SelectionItemPattern selection
                    && selection.Current.IsSelected;
                flags.Add(ok);
            }
        }
        catch
        {
            flags.Clear();
        }

        return flags;
    }

    /// <summary>
    /// 该窗口当前活动标签的地址，取自 Shell.Application 的 IWebBrowser2.LocationURL。
    /// 这只读、不操作，用来回答「Navigate2 到底落在哪」—— 标签名只说结果，地址能指认当前位置。
    /// </summary>
    private static string? ComLocationUrl(nint hwnd)
    {
        dynamic? shell = null;
        dynamic? windows = null;
        string? last = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            if (shell is null) return null;

            // 同一个 HWND 可能对应多个 IWebBrowser2（每个标签一个，实测 Win11 26300）：
            // 只返回第一个会读到空串。取「非空、且最后一个」—— Shell 枚举顺序里它通常是当前那个。
            windows = shell.Windows();
            foreach (dynamic w in windows)
            {
                try
                {
                    if ((long)w.HWND != hwnd) continue;
                    var url = (string)w.LocationURL;
                    if (!string.IsNullOrEmpty(url)) last = url;
                }
                catch
                {
                    // 窗口正在销毁时读属性会抛，跳过继续找。
                }
            }
        }
        catch
        {
            // 读不到就返回 null，不把它当成断言 —— 它只是诊断信息。
        }
        finally
        {
            ReleaseCom(windows);
            ReleaseCom(shell);
        }

        return last;
    }

    private static void ReleaseCom(object? com)
    {
        if (com is null) return;
        try
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(com);
        }
        catch
        {
            // 不影响结论。
        }
    }

    private static void Report(List<(string Name, bool Passed, string Detail)> results)
    {
        ProbeOutput.Section("汇总");
        foreach (var (name, passed, detail) in results)
        {
            var suffix = detail.Length > 0 && !passed ? $"  ({detail})" : "";
            ProbeOutput.Kv(name, (passed ? "PASS" : "FAIL") + suffix);
        }

        var allPassed = results.Count > 0 && results.All(r => r.Passed);
        ProbeOutput.Kv("总体", allPassed ? "PASS" : "FAIL");
        ProbeOutput.Flush();
    }
}
