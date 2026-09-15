using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;

namespace WinQuick.App.M0;

/// <summary>
/// 闪烁探针 —— 用屏幕像素回答「这件事到底闪不闪」。
///
/// 由来：用户前后报过三次闪烁，我每次都改掉一个**看起来**会让分层窗口重建表面的机制
/// （换 <c>Window.Content</c>、改窗口尺寸、显示顺序），但"闪"本身从来没被量过 ——
/// 一直在推断"这样会闪、那样不会"。而这个项目已经反复证明：推断出来的因果链，
/// 只要没有能失败的核对，就有一半是错的。
///
/// 所以换个办法：抓屏幕像素。闪是合成器在两帧之间取走了一块没准备好的表面，
/// 它只存在于屏幕上 —— 进程内部的任何布尔量都看不见它。
///
/// **采样必须在后台线程上连续跑。** 第一版把采样循环放在 UI 线程上，
/// 而触发动作（换菜单、重建视觉树）也是在 UI 线程上同步执行的 ——
/// 于是"动作执行的那一瞬间"正好是采样停摆的时候，而闪恰恰就发生在那儿。
/// 那一版量出来的"峰值 0"是**测得不准**，不是"没有闪"。
///
/// 判据：先在触发前采一段静止基线，再在触发后采一段。基线给出噪声底，
/// 触发后的峰值与基线同量级才算没闪。触发时刻打时间戳，所以能给出
/// "变化发生在动作之后多少毫秒"，也能分辨"藏一下又出现"（两段峰值）与
/// "一直藏着"（一段峰值且不恢复）。
///
/// 有一条对照检查「藏掉轮盘必须被检出」：检测器必须能失败，否则它说明不了任何事。
///
/// 用法：--flash-probe
/// </summary>
public static class FlashProbe
{
    private const int BaselineMs = 350;
    private const int AfterTriggerMs = 1400;

    /// <summary>连续重申的次数与间隔（间隔要大于一次采样，才分得清是哪一次引起的）。</summary>
    private const int RepeatCount = 15;

    private const int RepeatGapMs = 220;

    /// <summary>对话框那一条要采得更久 —— 关闭动作发生在触发后 1 秒开外。</summary>
    private const int DialogAfterTriggerMs = 2600;

    /// <summary>"前后逐像素相同"的判据：也走同一个噪声底。</summary>
    private const long SamePictureTolerance = 50_000;
    private const int RegionSize = 440;

    /// <summary>「明显变了」的判据：通道差之和超过它的像素才计入。</summary>
    private const int ChangedTolerance = 200;

    /// <summary>
    /// 噪声底：整块取样区的通道差总和低于它就当没变。
    ///
    /// 为什么需要它：换菜单之后画面里会有**亚像素级**的差异（重绘的抗锯齿落点），
    /// 实测约 2700 —— 但那是一个像素偏了几个色阶，眼睛看不见，也不是"闪"。
    /// 而真正的闪是整块表面被取走，量级是几十万到几百万。
    /// （整块均匀变暗那种"每像素都只变一点"的情况由变化像素数和这个底一起兜住。）
    /// </summary>
    private const long NoiseFloor = 50_000;

    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("flash", "m4");
        ProbeOutput.Line("屏幕取样判「闪」：静止场景下，任何与前后帧的差异都只能来自呈现层。");

        var results = new List<(string Name, bool Ok, string Detail)>();
        OverlayController? overlay = null;

        try
        {
            var menu = DefaultMenu.Build();
            overlay = new OverlayController(menu);
            overlay.Prewarm();

            var monitor = Diagnostics.MonitorLayout.AtCursor();
            PInvoke.SetCursorPos(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2);
            await Task.Delay(300);

            overlay.Show();
            await Task.Delay(1200);
            overlay.OpenSettings();
            await Task.Delay(1200);

            var regions = new List<Region>();
            if (CenterRegion("轮盘区", overlay.RadialPhysicalBounds) is { } wheel) regions.Add(wheel);
            if (CenterRegion("面板区", overlay.SettingsPanelPhysicalBoundsForProbe) is { } panel) regions.Add(panel);
            if (FreeCornerRegion("遮罩角", overlay, monitor) is { } corner) regions.Add(corner);

            // 文件对话框是按 owner（＝轮盘窗口）居中的，所以它会盖住轮盘中心 ——
            // 量"对话框进出"时必须取轮盘**露在对话框外面**的那一圈，否则量到的是
            // "被盖住了"（那也是变化，但不是闪，混在一起没法判读）。
            var exposed = ExposedBand(overlay.RadialPhysicalBounds);
            ProbeOutput.Kv("轮盘区 / 面板区 / 遮罩角", string.Join("；", regions.Select(r => r.ToString())));
            ProbeOutput.Kv("轮盘露出的上沿（对话框进出时用它）", exposed?.ToString() ?? "（没有）");

            if (regions.Count == 0)
            {
                results.Add(("拿到取样区域", false, "一个区域都没拿到"));
                ProbeOutput.Flush();
                return;
            }

            var primary = regions[0].Name;

            // ---- 测量内核 ---------------------------------------------------
            // 后台线程连续采样；UI 线程上触发动作并记下时刻。两者用同一个时钟
            //（Stopwatch 时间戳），所以"变化发生在触发后多少毫秒"是可信的。
            async Task<Dictionary<string, RegionLog>> CaptureAsync(
                IReadOnlyList<Region> targets, Func<Task> trigger, int afterMs = AfterTriggerMs)
            {
                var samplers = targets.Select(r => (Region: r, Sampler: new ScreenSampler(r.X, r.Y, r.W, r.H))).ToList();
                var logs = targets.ToDictionary(r => r.Name, _ => new RegionLog());

                foreach (var (_, sampler) in samplers) sampler.Sample();   // 丢弃第一帧（必然"全变"）

                var running = true;
                var worker = Task.Run(() =>
                {
                    while (Volatile.Read(ref running))
                    {
                        foreach (var (region, sampler) in samplers)
                        {
                            var (diff, changed) = sampler.Sample();
                            var bounds = sampler.LastChangedBounds is { } b
                                ? $"行 {b.MinRow}..{b.MaxRow} 列 {b.MinCol}..{b.MaxCol}"
                                : "";
                            logs[region.Name].Add(Stopwatch.GetTimestamp(), diff, changed, bounds);
                        }
                    }
                });

                try
                {
                    await Task.Delay(BaselineMs);

                    var triggerAt = Stopwatch.GetTimestamp();
                    await trigger();
                    var triggerDurationMs = MsBetween(triggerAt, Stopwatch.GetTimestamp());

                    foreach (var log in logs.Values)
                    {
                        log.TriggerDurationMs = triggerDurationMs;
                        log.MarkTrigger(triggerAt);
                    }

                    await Task.Delay(afterMs);
                    foreach (var log in logs.Values) log.Summarize();
                }
                finally
                {
                    Volatile.Write(ref running, false);
                    await worker;
                    foreach (var (_, sampler) in samplers) sampler.Dispose();
                }

                foreach (var region in targets)
                {
                    var log = logs[region.Name];
                    ProbeOutput.Kv(region.Name, log.Describe());
                    ProbeOutput.Line("      时间线：" + log.Timeline());
                }

                return logs;
            }

            void Add(string label, Dictionary<string, RegionLog> logs, string regionName)
            {
                var log = logs[regionName];
                results.Add((label, !log.Changed,
                    $"{regionName}：基线 {log.PeakBefore}，触发后 {log.PeakAfter}（噪声底 {NoiseFloor}），"
                    + $"变化帧 {log.SpikeCount}"));
            }

            // ---- 对照：检测器必须能失败 ------------------------------------
            {
                ProbeOutput.Section("对照：检测器能不能看见变化");
                var logs = await CaptureAsync(regions, () =>
                {
                    overlay.Hide();
                    return Task.CompletedTask;
                });

                var log = logs[primary];
                results.Add(("对照：藏掉覆盖层能被检出（检测器有效）",
                    log.Changed && log.SpikeCount > 0,
                    $"{primary} 触发后峰值 {log.PeakAfter}，变化帧 {log.SpikeCount}"));

                overlay.Show();
                await Task.Delay(1200);
                overlay.OpenSettings();
                await Task.Delay(1200);
            }

            // ---- 实验一：全栈 z 序重申 --------------------------------------
            {
                ProbeOutput.Section("z 序重申（会发多次 SetWindowPos）");
                var zBefore = overlay.ZOrder.ReassertCount;
                var surfaceBefore = SurfaceOps.Now();

                var logs = await CaptureAsync(regions, () =>
                {
                    overlay.ReassertTopmostForProbe();
                    return Task.CompletedTask;
                });

                var zDelta = overlay.ZOrder.ReassertCount - zBefore;
                ProbeOutput.Kv("期间重申 / 窗口级操作", $"{zDelta} 次 / {SurfaceOps.Now().DeltaFrom(surfaceBefore)}");
                if (zDelta == 0) results.Add(("重申确实发生了（否则下面几条是空跑）", false, "重申计数没涨"));

                Add("z 序重申不改变轮盘像素", logs, "轮盘区");
                if (logs.ContainsKey("面板区")) Add("z 序重申不改变面板像素", logs, "面板区");
                if (logs.ContainsKey("遮罩角")) Add("z 序重申不改变遮罩像素", logs, "遮罩角");
            }

            // ---- 实验一点五：连续重申 15 次 --------------------------------
            // 单次重申在两次跑里一次"没变"、一次"面板变了 2 帧" —— 间歇。
            // 间歇最容易被当成噪声放过去，而用户说的"频繁闪"正好是间歇的形状。
            // 所以这里连做 15 次，数清楚有多少次真的改了画面。
            {
                ProbeOutput.Section("连续重申 15 次");
                var marks = new List<long>();
                var zBefore = overlay.ZOrder.ReassertCount;

                var logs = await CaptureAsync(regions, async () =>
                {
                    for (var i = 0; i < RepeatCount; i++)
                    {
                        marks.Add(Stopwatch.GetTimestamp());
                        overlay.ReassertTopmostForProbe();
                        await Task.Delay(RepeatGapMs);
                    }
                }, afterMs: 900);

                var first = marks[0];
                ProbeOutput.Kv("触发时刻（相对第一次）",
                    string.Join(" ", marks.Select(m => $"{MsBetween(first, m):0}")));
                ProbeOutput.Kv("重申次数", (overlay.ZOrder.ReassertCount - zBefore).ToString());

                foreach (var name in new[] { "轮盘区", "面板区" })
                {
                    if (!logs.ContainsKey(name)) continue;
                    var log = logs[name];
                    results.Add(($"连续 {RepeatCount} 次重申都没有改变「{name}」的画面",
                        !log.Changed,
                        $"基线 {log.PeakBefore}，触发后 {log.PeakAfter}（噪声底 {NoiseFloor}），变化帧 {log.SpikeCount}"));
                }
            }

            // ---- 实验二：换菜单（内容刷新的代表） ---------------------------
            {
                ProbeOutput.Section("换菜单（就地重建视觉树）");
                var logs = await CaptureAsync(regions, () =>
                {
                    overlay.RebuildForMenu(menu);
                    return Task.CompletedTask;
                });
                Add("换菜单不改变轮盘像素", logs, "轮盘区");
            }

            // ---- 实验三：设置面板改文字 -------------------------------------
            if (regions.Any(r => r.Name == "面板区"))
            {
                ProbeOutput.Section("面板改文字");
                var logs = await CaptureAsync(regions, () =>
                {
                    overlay.SettingsPanelForProbe?.SetFooterForProbe("已保存。");
                    return Task.CompletedTask;
                });
                Add("面板改文字不改变面板像素", logs, "面板区");
            }

            // ---- 实验三点五：应用侧的入场状态序列 ---------------------------
            // 屏幕序列受合成与采样间隔影响，判读不唯一。这里直接读应用侧的状态：
            // "先闪一下终态"就表现为不透明度**先到 1 又回 0**，形状明确、没有歧义。
            {
                ProbeOutput.Section("入场状态（应用侧逐帧）");
                overlay.CloseSettings();
                await Task.Delay(600);

                async Task<(List<double> Opacity, List<double> Scale)> SampleEntranceAsync(
                    string label, Func<(double Scale, double Opacity)> read, Func<Task> trigger)
                {
                    var opacities = new List<double>();
                    var scales = new List<double>();
                    var opacityAfterTrigger = new List<double>();
                    var triggerAt = long.MaxValue;

                    var timer = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Render)
                    {
                        Interval = TimeSpan.FromMilliseconds(8),
                    };

                    timer.Tick += (_, _) =>
                    {
                        var (scale, opacity) = read();
                        scales.Add(scale);
                        opacities.Add(opacity);

                        // 触发之前读到的是"上一次留下的状态"（面板收起时仍是 1），
                        // 拿它做判据会得出"先满再回落"的假结论。
                        if (Stopwatch.GetTimestamp() >= triggerAt) opacityAfterTrigger.Add(opacity);
                    };

                    timer.Start();
                    triggerAt = Stopwatch.GetTimestamp();
                    await trigger();
                    await Task.Delay(500);
                    timer.Stop();

                    ProbeOutput.Kv(label,
                        $"共 {opacities.Count} 帧（触发后 {opacityAfterTrigger.Count} 帧）；"
                        + $"触发后首帧 {opacityAfterTrigger.FirstOrDefault():0.###}，"
                        + $"最大值 {opacityAfterTrigger.DefaultIfEmpty(0).Max():0.###}，"
                        + $"末帧 {opacityAfterTrigger.LastOrDefault():0.###}");
                    ProbeOutput.Line("      不透明度：" + string.Join(" ",
                        opacities.Take(30).Select(o => o.ToString("0.##"))));
                    ProbeOutput.Line("      缩放：" + string.Join(" ",
                        scales.Take(30).Select(x => x.ToString("0.###"))));

                    return (opacityAfterTrigger, scales);
                }

                if (overlay.SettingsPanelForProbe is { } settingsPanel)
                {
                    // 面板：先把覆盖层与面板都藏起来，走一次真实的"打开面板"。
                    overlay.Hide();
                    await Task.Delay(700);
                    overlay.Show();
                    await Task.Delay(1200);

                    var (opacities, _) = await SampleEntranceAsync(
                        "打开设置面板", () => settingsPanel.EntranceStateForProbe, () =>
                        {
                            overlay.OpenSettings();
                            return Task.CompletedTask;
                        });

                    // 判据只有一条：**第一帧不能已经是终态**。
                    // （"曲线是不是够柔和"是观感问题，不该在这里判真假。）
                    var firstAfterTrigger = opacities.FirstOrDefault(double.NaN);
                    results.Add(("面板入场的第一帧不是终态（不会先闪一下完整的界面）",
                        !double.IsNaN(firstAfterTrigger) && firstAfterTrigger < 0.5,
                        $"触发后首帧不透明度 {firstAfterTrigger:0.###}"));

                    await Task.Delay(500);
                }

                var radialEntrance = overlay.RadialEntranceStateForProbe;
                await Task.Delay(200);
                overlay.Hide();
                await Task.Delay(900);

                await SampleEntranceAsync("打开覆盖层", () => radialEntrance(), () =>
                {
                    overlay.Show();
                    return Task.CompletedTask;
                });

                await Task.Delay(600);
                overlay.OpenSettings();
                await Task.Delay(900);
            }

            // ---- 实验四：打开时第一帧是不是终态 -----------------------------
            // 用户报的"先显示设置面板/轮盘，再播入场缩放动画，看起来像闪了一下"。
            // 判据：以**隐藏状态**为基线，逐帧数"与基线不同的像素数"（≈ 可见面积 × 不透明度）。
            // 正确的入场是从 0 单调长到满；"先闪一下终态再缩放"会表现为序列先大后小。
            async Task<(int FirstVisible, int Max, bool Monotonic, string Series)> MeasureEntranceAsync(
                Region region, Func<Task> trigger, string label)
            {
                using var sampler = new ScreenSampler(region.X, region.Y, region.W, region.H);
                var baseline = sampler.CaptureFrame();

                var frames = new List<(long At, byte[] Frame)>();
                var running = true;
                var worker = Task.Run(() =>
                {
                    while (Volatile.Read(ref running))
                    {
                        var frame = sampler.CaptureFrame();
                        lock (frames) frames.Add((Stopwatch.GetTimestamp(), frame));
                    }
                });

                try
                {
                    await Task.Delay(300);
                    var triggerAt = Stopwatch.GetTimestamp();
                    await trigger();
                    await Task.Delay(700);
                    GC.KeepAlive(triggerAt);
                }
                finally
                {
                    Volatile.Write(ref running, false);
                    await worker;
                }

                // 用**差异幅度**而不是"变化像素数"：后者是二值的 ——
                // 轮盘一出现，整块都算"变了"，一帧就跳到满值，分辨不了缩放与淡入。
                // 幅度随"面积 × 不透明度"连续变化，才能看出入场是从小到大的。
                var magnitudes = new List<long>();
                lock (frames)
                {
                    foreach (var (_, frame) in frames)
                    {
                        var (diff, _) = ScreenSampler.Compare(baseline, frame, region.W, region.H);
                        magnitudes.Add(diff);
                    }
                }

                var peak = magnitudes.Count == 0 ? 0 : magnitudes.Max();

                // 从"第一帧看得出东西"开始截：之前的都是触发还没生效的空窗。
                var start = magnitudes.FindIndex(d => d > peak * 0.02);
                if (start < 0) start = 0;
                var series = magnitudes.Skip(start).Take(30).Select(d => (int)d).ToList();
                var firstVisible = series.Count > 0 ? series[0] : 0;

                var monotonic = true;
                for (var i = 1; i < series.Count; i++)
                {
                    if (series[i] < series[i - 1] - peak * 0.10) monotonic = false;
                }

                ProbeOutput.Kv(label,
                    $"峰值 {peak}，入场首帧 {firstVisible}（占峰值 {(peak == 0 ? 0 : firstVisible * 100 / peak)}%），"
                    + $"单调递增={monotonic}（共 {magnitudes.Count} 帧）");
                ProbeOutput.Line("      逐帧幅度：" + string.Join(" ", series));

                return (firstVisible, (int)peak, monotonic, string.Join(" ", series));
            }

            {
                ProbeOutput.Section("打开时的入场：第一帧是不是终态");
                overlay.CloseSettings();
                await Task.Delay(600);
                overlay.Hide();
                await Task.Delay(900);

                if (regions.Any(r => r.Name == "轮盘区"))
                {
                    var wheelRegion = regions.First(r => r.Name == "轮盘区");
                    var result = await MeasureEntranceAsync(wheelRegion,
                        () =>
                        {
                            overlay.Show();
                            return Task.CompletedTask;
                        },
                        "打开覆盖层");

                    // 首帧可见面积必须**明显小于**峰值，且序列单调 —— 否则就是先闪了终态。
                    // **这里不下"是不是闪"的结论。**
                    // 屏幕幅度把两件事混在一起：入场曲线的形状，以及"UI 线程忙完之后
                    // 才呈现第一帧"造成的延迟。两者都会让首帧幅度偏大。
                    // 真正能判真假的判据是应用侧的不透明度序列（上一节），
                    // 这里保留数据是为了改动曲线/调度后能对比。
                    ProbeOutput.Kv("（仅供参考，不作判定）覆盖层入场",
                        $"首帧 {result.FirstVisible} / 峰值 {result.Max}，单调={result.Monotonic}");

                    await Task.Delay(900);
                }

                if (regions.Any(r => r.Name == "面板区"))
                {
                    var panelRegion = regions.First(r => r.Name == "面板区");
                    var result = await MeasureEntranceAsync(panelRegion,
                        () =>
                        {
                            overlay.OpenSettings();
                            return Task.CompletedTask;
                        },
                        "打开设置面板");

                    ProbeOutput.Kv("（仅供参考，不作判定）面板入场",
                        $"首帧 {result.FirstVisible} / 峰值 {result.Max}，单调={result.Monotonic}");

                    await Task.Delay(900);
                    overlay.Hide();
                    await Task.Delay(900);
                    overlay.Show();
                    await Task.Delay(1200);
                    overlay.OpenSettings();
                    await Task.Delay(1200);
                }
            }

            // ---- 实验五：加一项 —— 文件对话框的进出 -------------------------
            if (exposed is { } band)
            {
                ProbeOutput.Section("加一项：文件对话框的进出");

                var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;

                // 状态时间线：像素变了之后要能立刻回答"是轮盘消失了、移动了，
                // 还是内容变了"。只靠像素差是分不出来的（三者都表现为像素变）。
                var stateSamples = new List<(long At, string Text)>();
                var stateRunning = true;
                _ = Task.Run(async () =>
                {
                    while (Volatile.Read(ref stateRunning))
                    {
                        var text = ui.Invoke(() =>
                        {
                            var b = overlay.RadialPhysicalBounds;
                            return $"可见={overlay.IsVisible} 面板={overlay.IsSettingsOpen} "
                                 + $"矩形={(b is { } r ? $"{r.X},{r.Y}" : "无")}";
                        });

                        lock (stateSamples) stateSamples.Add((Stopwatch.GetTimestamp(), text));
                        await Task.Delay(90);
                    }
                });

                // 触发前先拍一帧：后面要和"一切都结束之后"的那一帧逐像素比。
                // 相邻帧差只能说明"什么时候变了"，说明不了"变完有没有回到原样" ——
                // 而用户说的"闪"正是"变了一下又变回来"。
                using var beforeSampler = new ScreenSampler(band.X, band.Y, band.W, band.H);
                var beforeFrame = beforeSampler.CaptureFrame();

                var bandLogs = await CaptureAsync([band, .. regions.Where(r => r.Name == "遮罩角")], async () =>
                {
                    // 对话框由后台任务关掉：Pick 会一直阻塞在对话框上，而我们要采到它结束。
                    _ = Task.Run(async () =>
                    {
                        for (var i = 0; i < 150; i++)
                        {
                            var hwnd = ui.Invoke(() => overlay.OwnedSystemDialogForProbe());
                            if (hwnd != 0)
                            {
                                await Task.Delay(700);
                                PInvoke.PostMessage((HWND)hwnd, PInvoke.WM_CLOSE, default, default);
                                ProbeOutput.Kv("  对话框被关闭", $"0x{hwnd:X}");
                                return;
                            }

                            await Task.Delay(40);
                        }

                        ProbeOutput.Kv("  没等到对话框窗口", "（这条实验是空跑）");
                    });

                    // Pick 会阻塞 UI 线程（模态），所以派发出去 —— 采样在别的线程上，不受影响。
                    ui.BeginInvoke(new Action(() => overlay.PickExecutableForMenu()));
                    await Task.CompletedTask;
                }, DialogAfterTriggerMs);

                Volatile.Write(ref stateRunning, false);

                // 收尾那一帧：对话框已经关掉、动画也该落定了（CaptureAsync 内部又等了 700ms）。
                await Task.Delay(700);
                using var afterSampler = new ScreenSampler(band.X, band.Y, band.W, band.H);
                var afterFrame = afterSampler.CaptureFrame();
                var (sameDiff, sameChanged) = ScreenSampler.Compare(beforeFrame, afterFrame, band.W, band.H);

                ProbeOutput.Kv("加一项前后轮盘上沿的差异",
                    $"差 {sameDiff}（噪声底 {SamePictureTolerance}），变化像素 {sameChanged}");

                results.Add(("加一项之后轮盘画面与之前逐像素相同",
                    sameDiff < SamePictureTolerance,
                    $"差 {sameDiff}，变化像素 {sameChanged}"));

                // 只打状态**发生变化**的那些采样点，中间重复的省略 —— 一屏里能读完。
                var triggerAt = bandLogs[band.Name].TriggerAt;
                string? previous = null;
                foreach (var (at, text) in stateSamples)
                {
                    if (text == previous) continue;
                    ProbeOutput.Line($"      状态 {MsBetween(triggerAt, at):+0;-0} ms：{text}");
                    previous = text;
                }

                // 这里**不能**核"对话框期间轮盘上沿不变"：文件对话框比轮盘还大，
                // 轮盘上没有一处不被它盖住 —— 盖住当然会让像素变，但那是对话框，
                // 不是轮盘在闪。所以只核"整件事结束之后轮盘与之前逐像素相同"（在上面）。
                if (bandLogs.ContainsKey("遮罩角")) Add("弹文件对话框不改变遮罩像素", bandLogs, "遮罩角");
            }

            overlay.Hide();
        }
        catch (Exception ex)
        {
            results.Add(("探针完整跑完", false, ex.Message));
            ProbeOutput.Line();
            ProbeOutput.Line("探针未捕获异常: " + ex);
        }
        finally
        {
            overlay?.Dispose();
        }

        ProbeOutput.Section("结果");
        var failed = 0;
        foreach (var (name, ok, detail) in results)
        {
            ProbeOutput.Kv((ok ? "通过  " : "不通过") + " " + name, detail);
            if (!ok) failed++;
        }

        ProbeOutput.Line();
        ProbeOutput.Line(failed == 0
            ? $"  >>> {results.Count} 项全部通过。"
            : $"  >>> {failed} / {results.Count} 项不通过，见上。");

        ProbeOutput.Flush();
    }

    /// <summary>两个时间戳之间差多少毫秒。<b>两个都是时间戳</b> —— 早先这里只收一个参数，
    /// 结果调用方把"差值"传了进来，被又减了一次当前时间，印出四亿毫秒。</summary>
    private static double MsBetween(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;

    private readonly record struct Region(string Name, int X, int Y, int W, int H);

    /// <summary>一块取样区在两段时间里的表现。</summary>
    private sealed class RegionLog
    {
        private readonly List<(long At, long Diff, int Changed, string Bounds)> _samples = [];
        private long _triggerAt = long.MinValue;

        public long PeakBefore { get; private set; }
        public long PeakAfter { get; private set; }
        public int SpikeCount { get; private set; }
        public double TriggerDurationMs { get; set; }

        public void Add(long at, long diff, int changed, string bounds)
        {
            lock (_samples) _samples.Add((at, diff, changed, bounds));
        }

        /// <summary>只记下触发时刻。**不要在这里统计** —— 触发刚回来时，
        /// 之后的采样还没到，那时算出来的"触发后峰值"只覆盖开头几毫秒，
        /// 而闪往往在更晚一点的地方（比如退场动画、重建之后的第一次呈现）。</summary>
        public void MarkTrigger(long at) => _triggerAt = at;

        /// <summary>采样窗口结束后汇总。这时样本才齐。</summary>
        public void Summarize()
        {
            PeakBefore = 0;
            PeakAfter = 0;
            SpikeCount = 0;

            lock (_samples)
            {
                foreach (var (sampleAt, diff, changed, _) in _samples)
                {
                    if (sampleAt < _triggerAt)
                    {
                        if (diff > PeakBefore) PeakBefore = diff;
                    }
                    else
                    {
                        if (diff > PeakAfter) PeakAfter = diff;
                        if (changed > ChangedTolerance) SpikeCount++;
                    }
                }
            }
        }

        /// <summary>样本数（打印用）。</summary>
        public int SampleCount
        {
            get { lock (_samples) return _samples.Count; }
        }

        /// <summary>触发时刻（时间戳）。打印状态时间线时要把两者对齐。</summary>
        public long TriggerAt => _triggerAt;

        /// <summary>超过噪声底就算"看得见的变化"。</summary>
        public bool Changed => PeakAfter >= NoiseFloor;

        public string Describe() =>
            $"采样 {SampleCount} 帧；基线峰值 {PeakBefore}，触发后峰值 {PeakAfter}，"
            + $"变化帧 {SpikeCount}（触发耗时 {TriggerDurationMs:0} ms）";

        /// <summary>
        /// 变化帧的时间线（相对触发时刻的毫秒）。峰值只说"变了多少"，
        /// 时间线才说"变了几次、什么时候恢复" —— 「藏一下又出现」与「一直藏着」
        /// 的峰值可能一样，含义完全不同。
        /// </summary>
        public string Timeline()
        {
            List<(long At, long Diff, int Changed, string Bounds)> spikes;
            lock (_samples)
            {
                spikes = _samples.Where(s => s.Changed > ChangedTolerance).ToList();
            }

            if (spikes.Count == 0) return "（没有任何一帧变化超过 200 像素）";

            // 相邻的变化帧合并成一段，避免一次持续变化刷出几百行。
            var runs = new List<string>();
            var runStart = spikes[0];
            var runEnd = spikes[0];
            var peak = spikes[0].Diff;

            foreach (var spike in spikes.Skip(1))
            {
                if (spike.At - runEnd.At < Stopwatch.Frequency / 20)   // 50ms 内算同一段
                {
                    runEnd = spike;
                    if (spike.Diff > peak) peak = spike.Diff;
                }
                else
                {
                    runs.Add(Describe(runStart, runEnd, peak));
                    runStart = runEnd = spike;
                    peak = spike.Diff;
                }
            }

            runs.Add(Describe(runStart, runEnd, peak));
            return string.Join("，", runs);
        }

        private string Describe(
            (long At, long Diff, int Changed, string Bounds) from,
            (long At, long Diff, int Changed, string Bounds) to, long peak)
        {
            var start = MsBetween(_triggerAt, from.At);
            var end = MsBetween(_triggerAt, to.At);

            // 变化的**形状**比幅度更能说明问题：一条横带、整块、还是一角。
            var bounds = string.IsNullOrEmpty(to.Bounds) ? "" : $"，区域 {to.Bounds}";
            return $"触发后 {start:+0;-0}…{end:+0;-0} ms（峰值 {peak}{bounds}）";
        }
    }

    /// <summary>取某个矩形中心附近固定边长的一块；拿不到矩形时返回 null。</summary>
    private static Region? CenterRegion(string name, (int X, int Y, int Width, int Height)? rect)
    {
        if (rect is not { } r || r.Width <= 0 || r.Height <= 0) return null;

        var size = Math.Min(RegionSize, Math.Min(r.Width, r.Height));
        return new Region(name, r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
    }

    /// <summary>
    /// 轮盘上沿露出来的一条：文件对话框按 owner 居中，盖住轮盘的中间，
    /// 但上下边缘是露着的，所以只有这条带子能量"轮盘自己在对话框期间有没有变"。
    /// </summary>
    private static Region? ExposedBand((int X, int Y, int Width, int Height)? rect)
    {
        if (rect is not { } r || r.Width < 400 || r.Height < 400) return null;

        const int width = 300;
        const int height = 150;
        return new Region("轮盘上沿", r.X + (r.Width - width) / 2, r.Y + 10, width, height);
    }

    /// <summary>
    /// 挑一个屏幕角落做"遮罩区"取样点 —— 要求那一块上面没有我们自己的窗口，
    /// 否则量的是自己的窗口而不是遮罩。四个角依次试，取第一个干净的。
    /// </summary>
    private static Region? FreeCornerRegion(string name, OverlayController overlay, MonitorInfo monitor)
    {
        const int margin = 8;
        Span<(int X, int Y)> corners =
        [
            (monitor.X + margin, monitor.Bottom - RegionSize - margin),
            (monitor.Right - RegionSize - margin, monitor.Bottom - RegionSize - margin),
            (monitor.Right - RegionSize - margin, monitor.Y + margin),
            (monitor.X + margin, monitor.Y + margin),
        ];

        foreach (var (x, y) in corners)
        {
            var center = new System.Drawing.Point(x + RegionSize / 2, y + RegionSize / 2);
            var hwnd = PInvoke.WindowFromPoint(center);
            if (!overlay.IsOwnWindowForProbe((nint)hwnd))
                return new Region(name, x, y, RegionSize, RegionSize);
        }

        return null;
    }
}
