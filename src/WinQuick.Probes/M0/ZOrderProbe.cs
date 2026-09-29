using System.IO;
using System.Text;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.M0;

/// <summary>
/// z 序与可点击性探针。
///
/// 存在的理由：用户报告「遮罩、轮盘、设置面板一直在争抢顶层」以及「面板点不到」。
/// 这类问题的症状是「偶尔被盖住」「某个位置点不动」，靠人手很难稳定复现，
/// 也无法在改完之后证明真的修好了。所以先用两个系统 API 把现状变成数字：
///
/// 1. **`GetWindow(GW_HWNDNEXT)` 沿顶层窗口链遍历** —— 读出真实的 z 序，
///    而不是相信我们自己设的 `Topmost=true`。窗口样式写着 topmost 不代表它就在最上面。
/// 2. **`WindowFromPoint`** —— 直接回答「这个坐标上的点击会被哪个窗口拿走」。
///    这是「看得见却点不动」唯一可靠的判据：视觉层级由 DWM 决定，命中层级由这个决定，
///    两者可以不一致，而不一致时用户看到的就是「点不动」。
///
/// 先跑一遍拿到「现状基线」，改完再跑一遍证明修好了。
/// </summary>
public static class ZOrderProbe
{
    /// <summary>窗口在我们的层级栈里期望的顺序。索引越小越靠上。</summary>
    private static readonly string[] ExpectedTopToBottom =
        ["设置面板", "操作条", "轮盘", "遮罩"];

    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("zorder", "m4");
        ProbeOutput.Line("核对真实的 z 序与落点，而不是我们自己设的样式位。");

        OverlayController? overlay = null;
        try
        {
            overlay = new OverlayController(DefaultMenuForProbe());
            overlay.SetSettingsSource(new AppConfig());

            overlay.Prewarm();
            overlay.StartZOrderWatching();

            ProbeOutput.Section("覆盖层未打开时");
            ReportWindows(overlay, "未打开");

            overlay.Show();
            await Task.Delay(500);

            ProbeOutput.Section("覆盖层打开后");
            ReportWindows(overlay, "已打开");

            ProbeOutput.Section("z 序实测（沿顶层窗口链遍历）");
            ReportZOrder(overlay);

            ProbeOutput.Section("落点核对（WindowFromPoint）");
            ReportHitTests(overlay);

            // 设置面板打开 —— 这是用户报告问题的主场景。
            overlay.OpenSettings();
            await Task.Delay(600);

            ProbeOutput.Section("设置面板打开后");
            ReportWindows(overlay, "面板已开");

            ProbeOutput.Section("设置面板打开后的 z 序");
            ReportZOrder(overlay);

            ProbeOutput.Section("设置面板打开后的落点核对");
            ReportHitTests(overlay);

            // 静置核对：设置面板开着时，z 序重申与 SetWindowPos 应当几乎不发生。
            // 无条件反复重申会让分层窗口持续重新合成 —— 那正是用户报的"一直闪烁"。
            ProbeOutput.Section("静置 3 秒：是否还在反复重申 z 序");
            var beforeCount = overlay.ZOrder.ReassertCount;
            var beforeSkipped = overlay.ZOrder.SkippedCount;
            await Task.Delay(3000);
            var ranDuringIdle = overlay.ZOrder.ReassertCount - beforeCount;
            var skippedDuringIdle = overlay.ZOrder.SkippedCount - beforeSkipped;

            ProbeOutput.Kv("静置期间实际重申次数", ranDuringIdle.ToString());
            ProbeOutput.Kv("静置期间判定无需重申的次数", skippedDuringIdle.ToString());
            ProbeOutput.Kv("定时器触发次数（约）", (3000 / 800.0).ToString("0"));
            ProbeOutput.Line();
            ProbeOutput.Line(ranDuringIdle == 0
                ? "  >>> 静置期间没有重申 —— 分层窗口不会被反复重新合成，不再因此闪烁。"
                : $"  >>> 静置期间仍重申了 {ranDuringIdle} 次，会造成持续闪烁。");

            // 菜单变更后轮盘位置是否被保留
            ProbeOutput.Section("菜单变更后轮盘位置是否保留");
            var beforeBounds = overlay.RadialPhysicalBounds;
            ProbeOutput.Kv("改前轮盘矩形", beforeBounds is { } b
                ? $"({b.X},{b.Y}) {b.Width}x{b.Height}" : "(取不到)");

            // 菜单变更走的就是"往菜单里加一项"这条路 —— 面板开着时用户在轮盘上点「＋」
            // 选完程序后触发的那一次重建。这里直接调重建，跳过的只是文件对话框。
            var menuWithExtra = Hosting.DefaultMenu.Build() with
            {
                Items = Hosting.DefaultMenu.Build().Items.Append(new Core.Config.MenuItemSpec
                {
                    Label = "探针新增项",
                    Action = Core.Config.MenuAction.Launch("cmd.exe"),
                }).ToList(),
            };

            overlay.RebuildForMenu(menuWithExtra);
            await Task.Delay(700);

            var afterBounds = overlay.RadialPhysicalBounds;
            ProbeOutput.Kv("改后轮盘矩形", afterBounds is { } a
                ? $"({a.X},{a.Y}) {a.Width}x{a.Height}" : "(取不到)");

            ProbeOutput.Line();
            ProbeOutput.Line(beforeBounds is not null && beforeBounds == afterBounds
                ? "  >>> 轮盘位置被保留，没有跳到光标处。"
                : "  >>> 轮盘位置变了 —— 菜单变更后它会跳到光标处、压住用户正在操作的面板。");

            // 菜单变更是**真正会重建遮罩**的路径（RebuildForMenu → RebuildDimWindows），
            // 也正是之前"重建出来的遮罩压住轮盘与面板"那个 bug 的触发点。必须守住这里。
            ProbeOutput.Section("菜单变更后（真正会重建遮罩窗口）");
            ReportZOrder(overlay);
            ReportHitTests(overlay);

            if (cli.Has("dialog")) await VerifyFileDialogAsync(overlay);
            else
            {
                ProbeOutput.Section("系统文件对话框");
                ProbeOutput.Line("  加 --dialog 才会实际弹出文件对话框（会真的开一个窗口再自动关掉）。");
            }

            // 拖动遮罩深浅：现在只就地改 alpha，**不重建窗口**。
            // 之前是"销毁整屏遮罩 + 重建"，每次滑动都让屏幕闪一下（用户报的"一直闪烁"）。
            ProbeOutput.Section("拖遮罩深浅（只改 alpha，不重建窗口）");
            var handleBefore = overlay.DimHandles.FirstOrDefault();
            ProbeOutput.Kv("改前遮罩句柄", $"0x{handleBefore:X}");

            overlay.ApplyDimOpacity(0.62);
            await Task.Delay(300);

            var handleAfter = overlay.DimHandles.FirstOrDefault();
            ProbeOutput.Kv("改后遮罩句柄", $"0x{handleAfter:X}");
            ProbeOutput.Line();
            ProbeOutput.Line(handleBefore == handleAfter
                ? "  >>> 遮罩窗口被复用（只更新了 alpha）—— 拖动滑块时不会整屏闪。"
                : "  >>> 遮罩窗口被重建了 —— 拖动滑块时会让整屏闪一下。");

            ProbeOutput.Flush();
        }
        catch (Exception ex)
        {
            ProbeOutput.Line();
            ProbeOutput.Line("探针异常: " + ex);
            CrashLog.Write("zorder-probe", ex);
            ProbeOutput.Flush();
        }
        finally
        {
            overlay?.Dispose();
        }
    }

    private static MenuSpec DefaultMenuForProbe() => MenuSpec.BuiltInDefault;

    /// <summary>
    /// 验证从置顶覆盖层里弹出的系统文件对话框不会被遮罩盖住。
    ///
    /// 官方语义是「一个窗口**变成** topmost 时，它拥有的窗口才连带变 topmost」——
    /// 我们的面板早就是 topmost 了，所以这个转变从未发生，对话框一直是非置顶的、
    /// 被遮罩压住。修法是让对话框成为层级栈的最顶层参与重申。
    ///
    /// 对话框是模态的、会阻塞调用线程，所以用 DispatcherTimer 在模态消息循环里做核对与收尾。
    /// </summary>
    private static async Task VerifyFileDialogAsync(OverlayController overlay)
    {
        ProbeOutput.Section("系统文件对话框");

        // 从这里弹出的对话框，与用户在轮盘上点「＋」时弹出的**是同一个**：
        // 都走 OverlayController.PickExecutableForMenu（所有者是轮盘窗口）。
        // 面板自己的那个入口已经移除，所以这个核对也应当跟着换到新路径上 ——
        // 否则核对的是一个不再存在的调用方。

        var dialogHwnd = nint.Zero;
        var topmostWhenChecked = false;
        var hitTest = "";

        var probe = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };

        probe.Tick += (_, _) =>
        {
            // 找当前由我们拥有的前台窗口 —— 那就是刚弹出的文件对话框。
            var fg = PInvoke.GetForegroundWindow();
            if (fg == default) return;

            var owner = PInvoke.GetWindow(fg, GET_WINDOW_CMD.GW_OWNER);
            var handles = new List<nint> { overlay.RadialHandle, overlay.ActionBarHandle, overlay.SettingsPanelHandle };
            handles.AddRange(overlay.DimHandles);
            if (!handles.Contains((IntPtr)owner)) return;

            dialogHwnd = (IntPtr)fg;
            var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(fg, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            topmostWhenChecked = ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);

            if (PInvoke.GetWindowRect(fg, out var rect))
            {
                var cx = (rect.left + rect.right) / 2;
                var cy = (rect.top + rect.bottom) / 2;
                var hit = PInvoke.WindowFromPoint(new System.Drawing.Point(cx, cy));
                var root = hit == default ? default : PInvoke.GetAncestor(hit, GET_ANCESTOR_FLAGS.GA_ROOT);
                hitTest = root == fg ? "对话框自己（点得到）" : $"被别人拿走 0x{root:X}";
            }

            // 收尾：关掉对话框，让 ShowDialog 返回。
            PInvoke.PostMessage(fg, PInvoke.WM_CLOSE, default, default);
            probe.Stop();
        };

        probe.Start();

        // 模态调用会阻塞这一行，但上面那个计时器会在模态消息循环里继续跑。
        // 弹的是真正的系统文件对话框，所有者是轮盘窗口。
        overlay.PickFileDialogForProbe();

        await Task.Delay(200);

        ProbeOutput.Kv("对话框句柄", dialogHwnd == 0 ? "(未捕获)" : $"0x{dialogHwnd:X}");
        ProbeOutput.Kv("对话框已置顶", dialogHwnd == 0 ? "(未捕获)" : (topmostWhenChecked ? "是" : "否"));
        ProbeOutput.Kv("对话框中心落点", hitTest.Length > 0 ? hitTest : "(未测)");

        ProbeOutput.Line();
        ProbeOutput.Line(dialogHwnd != 0 && topmostWhenChecked
            ? "  >>> 文件对话框已置顶，不会被遮罩盖住。"
            : "  >>> 文件对话框未能置顶 —— 它会被遮罩压住、点不到。");
    }

    // ---------------------------------------------------------------- 窗口清单

    private static void ReportWindows(OverlayController overlay, string stage)
    {
        ProbeOutput.Line($"  [{stage}]");
        foreach (var (name, hwnd) in EnumerateOurWindows(overlay))
        {
            if (hwnd == 0) { ProbeOutput.Kv("  " + name, "(句柄未创建)"); continue; }

            var topmost = HasTopmost(hwnd);
            var visible = PInvoke.IsWindowVisible((HWND)hwnd);
            ProbeOutput.Kv("  " + name,
                $"0x{hwnd:X}  topmost={(topmost ? "是" : "否")}  可见={(visible ? "是" : "否")}");
        }
    }

    private static IEnumerable<(string Name, nint Hwnd)> EnumerateOurWindows(OverlayController overlay)
    {
        yield return ("轮盘", overlay.RadialHandle);
        yield return ("操作条", overlay.ActionBarHandle);
        yield return ("设置面板", overlay.SettingsPanelHandle);

        var dims = overlay.DimHandles;
        for (var i = 0; i < dims.Count; i++)
            yield return ($"遮罩 #{i}", dims[i]);
    }

    private static bool HasTopmost(nint hwnd)
    {
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        return ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);
    }

    // ---------------------------------------------------------------- z 序

    /// <summary>
    /// 从顶层窗口链的最上面往下遍历，记下我们每个窗口出现的位置。
    /// 索引越小越靠上 —— 这才是「谁盖住谁」的真实答案。
    /// </summary>
    private static void ReportZOrder(OverlayController overlay)
    {
        var ours = EnumerateOurWindows(overlay)
            .Where(w => w.Hwnd != 0 && PInvoke.IsWindowVisible((HWND)w.Hwnd))
            .ToDictionary(w => w.Hwnd, w => w.Name);

        if (ours.Count == 0)
        {
            ProbeOutput.Line("  没有可见的窗口。");
            return;
        }

        var positions = new Dictionary<nint, int>();
        var index = 0;

        var hwnd = PInvoke.GetTopWindow(default);
        while (hwnd != default && index < 5000)
        {
            if (ours.ContainsKey(hwnd)) positions[hwnd] = index;
            hwnd = PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_HWNDNEXT);
            index++;
        }

        var ordered = positions.OrderBy(p => p.Value).ToList();

        ProbeOutput.Line($"  {"位次",-6} {"窗口",-12} 说明");
        foreach (var (handle, position) in ordered)
        {
            ProbeOutput.Line($"  {position,-6} {ours[handle],-12} 在全部顶层窗口中第 {position} 个");
        }

        ProbeOutput.Line();
        ProbeOutput.Line("  实际自上而下: " + string.Join(" > ", ordered.Select(p => ours[p.Key])));
        ProbeOutput.Line("  期望自上而下: " + string.Join(" > ", ExpectedTopToBottom.Where(n => ours.ContainsValue(n))));

        static string Normalize(string name) =>
            name.StartsWith("遮罩", StringComparison.Ordinal) ? "遮罩" : name;

        var actual = ordered.Select(p => Normalize(ours[p.Key])).ToList();
        var expected = ExpectedTopToBottom.Where(n => ours.ContainsKey(0) || ours.Values.Any(v => Normalize(v) == n)).ToList();

        var matches = actual.SequenceEqual(expected);
        ProbeOutput.Line();
        ProbeOutput.Line(matches
            ? "  >>> z 序与期望一致。"
            : "  >>> z 序与期望不一致 —— 上面层级更靠前的窗口会盖住后面的，"
              + "而「盖住」正是「点不到」的直接原因。");

        // 另外报告：我们最上面的窗口之上还有多少个别人的 topmost 窗口 ——
        // 那些会盖住我们（比如任务管理器）。
        var ourTop = ordered.FirstOrDefault().Value;
        var foreignAbove = 0;
        var probe = PInvoke.GetTopWindow(default);
        for (var i = 0; i < ourTop && probe != default; i++)
        {
            if (!ours.ContainsKey(probe) && HasTopmost(probe)) foreignAbove++;
            probe = PInvoke.GetWindow(probe, GET_WINDOW_CMD.GW_HWNDNEXT);
        }
        ProbeOutput.Line($"  我们之上还有 {foreignAbove} 个别人的 topmost 窗口（会盖住我们）。");
    }

    // ---------------------------------------------------------------- 落点

    /// <summary>
    /// 在几个关键位置问系统「这里的点击会被谁拿走」。
    ///
    /// 这是本轮最要紧的判据：视觉上看得见（DWM 把它们合成在一起）
    /// 与命中上收得到（窗口链决定）是两回事，不一致时就表现为「看得见却点不动」。
    /// </summary>
    private static void ReportHitTests(OverlayController overlay)
    {
        var names = EnumerateOurWindows(overlay)
            .Where(w => w.Hwnd != 0)
            .ToDictionary(w => w.Hwnd, w => w.Name);

        // 遮罩带序号（遮罩 #0…），期望里写的是"遮罩"，比对时按前缀归一。
        static bool NameMatches(string actual, string expected) =>
            actual == expected || actual.StartsWith(expected + " ", StringComparison.Ordinal);

        var samples = new List<(string Where, int X, int Y, string Expected)>
        {
            ("圆盘扇区（正上方）", 0, 0, "轮盘"),
            ("圆盘外的方形角落", 0, 0, "遮罩"),
            ("操作条「设置」按钮", 0, 0, "操作条"),
            ("面板中心", 0, 0, overlay.SettingsPanelHandle != 0 ? "设置面板" : "遮罩"),
            ("遮罩空白（远离一切）", 0, 0, "遮罩"),
        };

        // 用各窗口的真实矩形算出采样点，不在探针里猜布局。
        if (PInvoke.GetWindowRect((HWND)overlay.RadialHandle, out var wheel))
        {
            var cx = (wheel.left + wheel.right) / 2;
            var cy = (wheel.top + wheel.bottom) / 2;
            var tokens = Design.TokenStore.Current;
            var scale = MonitorLayout.AtCursor().Scale;
            var midRadius = (int)Math.Round(tokens.Radius.Ring1MidRadius * scale);

            samples[0] = ("圆盘扇区（正上方）", cx, cy - midRadius, "轮盘");
            // 窗口左上角内缩 20px：这个位置在 740 方形内、但在圆环之外
            samples[1] = ("圆盘外的方形角落", wheel.left + 20, wheel.top + 20, "遮罩");
        }

        if (overlay.ActionBarSettingsButtonScreenPoint() is { } barPoint)
            samples[2] = ("操作条「设置」按钮", barPoint.X, barPoint.Y, "操作条");

        var panelHandle = overlay.SettingsPanelHandle;
        if (panelHandle != 0 && PInvoke.IsWindowVisible((HWND)panelHandle)
            && PInvoke.GetWindowRect((HWND)panelHandle, out var panel))
        {
            samples[3] = ("面板中心", (panel.left + panel.right) / 2, (panel.top + panel.bottom) / 2, "设置面板");
        }

        var monitor = MonitorLayout.AtCursor();
        samples[4] = ("遮罩空白（远离一切）",
            monitor.X + monitor.Width / 2, monitor.Bottom - 40, "遮罩");

        ProbeOutput.Line($"  {"采样位置",-22} {"期望",-10} {"实际",-12} 结果");
        ProbeOutput.Line("  " + new string('-', 58));

        var failures = new List<string>();
        foreach (var (where, x, y, expected) in samples)
        {
            var hit = PInvoke.WindowFromPoint(new System.Drawing.Point(x, y));

            // 归一化到顶层窗口：命中测试可能返回子窗口
            var root = hit == default ? default : PInvoke.GetAncestor(hit, GET_ANCESTOR_FLAGS.GA_ROOT);
            var actual = names.TryGetValue(root, out var n) ? n : (root == default ? "(无)" : $"别人 0x{root:X}");

            var ok = NameMatches(actual, expected);
            if (!ok) failures.Add(where);

            ProbeOutput.Line($"  {where,-22} {expected,-10} {actual,-12} {(ok ? "√" : "× 点不到")}");
        }

        ProbeOutput.Line();
        if (failures.Count == 0)
        {
            ProbeOutput.Line("  >>> 所有采样位置的落点都正确。");
        }
        else
        {
            ProbeOutput.Line($"  >>> {failures.Count} 个位置点不到：{string.Join("、", failures)}");
            ProbeOutput.Line("      「看得见却点不动」的机制：视觉层级由 DWM 合成决定，");
            ProbeOutput.Line("      命中层级由窗口链决定，两者可以不一致。");
        }
    }
}
