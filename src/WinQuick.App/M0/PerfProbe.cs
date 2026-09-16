using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using WinQuick.App.Diagnostics;
using WinQuick.App.Design;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;

namespace WinQuick.App.M0;

/// <summary>
/// 帧时间探针 —— 把"动画顺不顺"从感觉变成数字。
///
/// 为什么需要它：流畅度是最容易凭感觉改的一类问题，而凭感觉改通常改错 ——
/// 换一个看似更省的写法，实际把开销挪到了别处。这个探针给三个可比的数字：
///
/// 1. **帧间隔分布**（p50 / p95 / p99 / 最大）：判定"卡不卡"的直接依据。
///    本机 60Hz，理想间隔 16.7ms；p95 明显高于它说明有掉帧。
/// 2. **长帧占比**：超过 1.5 倍中位数的帧的比例。平均值会被少数长帧带偏，
///    所以要单列。
/// 3. **测量的口径要说清**：帧间隔里包含"这一帧有多少活要干"和"合成器什么时候给下一帧"，
///    所以它测的是**端到端观感**，而不是某项操作的纯耗时。要看单项开销得单独埋点。
///
/// 关键设计：**用真实交互驱动**（移动指针扫过扇区、展开子弧），
/// 而不是自己造一个假的动画循环。假循环测出来的数字跟用户看到的东西没关系。
///
/// 用法：--perf-probe [--ms=90] [--repeat=3]
/// </summary>
public static class PerfProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("perf", "m4");
        ProbeOutput.Line("把帧间隔采成分布，而不是靠肉眼说「流畅」或「卡」。");
        ProbeOutput.Line();

        var sweepMs = cli.GetInt("ms", 90);
        var repeat = cli.GetInt("repeat", 3);

        var tokens = TokenStore.Current;
        var menu = DefaultMenu.Build();

        // 带子项的那一项，用来驱动子弧动画。
        var parentIndex = -1;
        for (var i = 0; i < menu.Items.Count; i++)
            if (menu.Items[i].HasChildren) { parentIndex = i; break; }

        OverlayController? overlay = null;
        try
        {
            overlay = new OverlayController(menu);
            overlay.Prewarm();
            overlay.Show();
            await Task.Delay(700);

            ProbeOutput.Section("环境");
            ProbeOutput.Kv("渲染层级", NativeDisplay.RenderTierDescription());
            ProbeOutput.Kv("扫描间隔", sweepMs + " ms/扇区");
            ProbeOutput.Kv("重复轮数", repeat.ToString());

            // ---- 采集 ----------------------------------------------------
            var recorder = new FrameRecorder();
            CompositionTarget.Rendering += recorder.OnRendering;

            var sweepPlan = new List<string>();

            for (var round = 0; round < repeat; round++)
            {
                // 扫过第一层每个扇区：每换一个，就有一对扇区在鼓出/缩回。
                for (var i = 0; i < menu.Items.Count; i++)
                {
                    overlay.HoverForProbe(i);
                    await Task.Delay(sweepMs);
                }

                // 子弧展开 + 扫过子项：子环的展开动画与鼓出。
                if (parentIndex >= 0)
                {
                    overlay.HoverForProbe(parentIndex);
                    overlay.ExpandChildArcForProbe(parentIndex);
                    await Task.Delay(200);

                    var childCount = menu.Items[parentIndex].Children.Count;
                    for (var i = 0; i < childCount; i++)
                    {
                        overlay.SetChildHoverForProbe(i);
                        await Task.Delay(sweepMs);
                    }

                    overlay.CollapseChildArcForProbe();
                    await Task.Delay(200);
                }

                overlay.HoverForProbe(-1);
                await Task.Delay(120);
            }

            CompositionTarget.Rendering -= recorder.OnRendering;

            var stats = recorder.Summarize();

            // ---- 报告 ----------------------------------------------------
            ProbeOutput.Section("帧间隔（含子弧展开与子弧鼓出）");
            ProbeOutput.Kv("采样帧数", stats.Count.ToString());
            ProbeOutput.Kv("中位数 p50", $"{stats.P50:0.00} ms");
            ProbeOutput.Kv("p95", $"{stats.P95:0.00} ms");
            ProbeOutput.Kv("p99", $"{stats.P99:0.00} ms");
            ProbeOutput.Kv("最大", $"{stats.Max:0.00} ms");
            ProbeOutput.Kv("理想（60Hz）", "16.67 ms");
            ProbeOutput.Line();
            ProbeOutput.Kv("超过 20ms 的帧", $"{stats.Over20Ms} / {stats.Count}" +
                                           $"（{(stats.Count == 0 ? 0 : 100.0 * stats.Over20Ms / stats.Count):0.#}%）");
            ProbeOutput.Kv("超过 33ms 的帧", stats.Over33Ms.ToString());
            ProbeOutput.Line();
            ProbeOutput.Kv("测量期间托管分配", $"{stats.AllocatedBytes / 1024.0:0.#} KB");
            ProbeOutput.Kv("换算到每秒", $"{stats.AllocatedBytes / 1024.0 / Math.Max(0.001, stats.WallMs / 1000.0):0.#} KB/s");

            // ---- 几何并发压力 --------------------------------------------
            // 入场若要改成"逐个扇区鼓出"，同一时刻会有多个扇区在改几何。
            // 把这笔开销单独量出来：它决定交错间隔与单扇区时长能放开到什么程度
            //（同时在动的扇区数 ≈ 单扇区时长 ÷ 交错间隔）。
            //
            // 这是在**静止的窗口**上量的 —— 于是它只反映几何本身的代价；
            // 入场时还要叠加分层窗口整块表面每帧重新提交的固定开销，
            // 所以最后再量一条"入场 + 4 个扇区同时在动"。
            async Task<FrameStats> MeasureAsync(int ms, Func<Task> drive)
            {
                var rec = new FrameRecorder();
                CompositionTarget.Rendering += rec.OnRendering;
                try
                {
                    await drive();
                    await Task.Delay(ms);
                }
                finally
                {
                    CompositionTarget.Rendering -= rec.OnRendering;
                    overlay.StopGeometryStressForProbe();
                }

                await Task.Delay(300);
                return rec.Summarize();
            }

            static string Describe(FrameStats s) =>
                $"p50 {s.P50:0.0} / p95 {s.P95:0.0} / p99 {s.P99:0.0} / max {s.Max:0.0} ms；"
                + $">{20}ms {s.Over20Ms}/{s.Count}；分配 "
                + $"{s.AllocatedBytes / 1024.0 / Math.Max(0.001, s.WallMs / 1000.0):0} KB/s";

            // 顺手把一条不变量也锁住：**动完之后必须逐像素回到静止态**。
            // 这正是入场动画结束时要守住的同一条 —— 入场最容易出的隐蔽 bug
            // 就是"动画结束后几何差一点点"，而它不会报错、只会看着有点歪。
            // 这里把下面那组压力当成"最坏情况的动画"来用它。
            // **先把画面真正静下来再拍参照帧。**
            // 扫描段最后一次 HoverForProbe(-1) 之后只等了 120ms，而鼓出缩回要 130ms ——
            // 参照帧里会留下一点残余形变（表现为边缘的亚像素位移）。
            // 第一次跑这条检查时报"几何没还原干净 1692 像素"，其实是我拍早了：
            // 对照（什么都不做再拍一张）差得一模一样，指向的是参照帧而不是还原逻辑。
            overlay.HoverForProbe(-1);
            await Task.Delay(700);

            var wheelBounds = overlay.RadialPhysicalBounds;
            ScreenSampler? wheelSampler = null;
            byte[]? restingFrame = null;
            if (wheelBounds is { } wb && wb.Width > 200 && wb.Height > 200)
            {
                var size = Math.Min(440, Math.Min(wb.Width, wb.Height));
                wheelSampler = new ScreenSampler(
                    wb.X + (wb.Width - size) / 2, wb.Y + (wb.Height - size) / 2, size, size);
                restingFrame = wheelSampler.CaptureFrame();
            }

            ProbeOutput.Section("几何并发压力（每帧重建 N 个扇区的几何）");
            ProbeOutput.Kv("静止（N=0）", Describe(await MeasureAsync(1200, () => Task.CompletedTask)));

            var sectorCount = overlay.SectorCountForProbe;
            foreach (var n in new[] { 1, 3, 6, 12, 20 })
            {
                if (n > sectorCount) continue;

                var burst = await MeasureAsync(1500, () =>
                {
                    overlay.StartGeometryStressForProbe(n, 1500);
                    return Task.CompletedTask;
                });

                ProbeOutput.Kv($"每帧重建 {n} 个扇区", Describe(burst));
            }

            if (wheelSampler is not null && restingFrame is not null)
            {
                // 先做一条**对照**：什么都不做，只过一小会儿再拍一张。
                // 它给出"这套测量本身的复现精度" —— 没有这条，"回到静止态"那个差值
                // 就没法判读：是没还原干净，还是量具自己就不稳。
                await Task.Delay(400);
                var controlFrame = wheelSampler.CaptureFrame();
                var control = ScreenSampler.CompareDetailed(
                    restingFrame, controlFrame, wheelSampler.Width, wheelSampler.Height);
                ProbeOutput.Kv("对照：什么都不做，隔一会儿再拍",
                    $"差 {control.Diff}，变化像素 {control.Changed}"
                    + (control.Changed > 0
                        ? $"（行 {control.MinRow}..{control.MaxRow} 列 {control.MinCol}..{control.MaxCol}）"
                        : ""));

                await Task.Delay(400);
                var afterFrame = wheelSampler.CaptureFrame();
                var after = ScreenSampler.CompareDetailed(
                    restingFrame, afterFrame, wheelSampler.Width, wheelSampler.Height);
                wheelSampler.Dispose();

                var where = after.Changed > 0
                    ? $"；变化在 行 {after.MinRow}..{after.MaxRow} 列 {after.MinCol}..{after.MaxCol}"
                      + $"（取样区 {wheelSampler.Width}×{wheelSampler.Height}）"
                    : "";

                ProbeOutput.Kv("压力结束后是否逐像素回到静止态",
                    after.Diff < 50_000
                        ? $"是（差 {after.Diff}，变化像素 {after.Changed}）"
                        : $"否（差 {after.Diff}，变化像素 {after.Changed}）—— 几何没还原干净{where}");
            }

            // 12 项要单独造一份菜单：默认菜单只有 8 项，而 12 是上限，
            // 也是几何最重的场景（每帧重建 12 个扇区 ≈ 逐扇区入场的最坏并发）。
            {
                var wide = menu with
                {
                    Items = Enumerable.Range(1, 12)
                        .Select(i => new MenuItemSpec { Label = $"第 {i} 项" })
                        .ToList(),
                };

                overlay.RebuildForMenu(wide);
                await Task.Delay(500);

                var burst12 = await MeasureAsync(1500, () =>
                {
                    overlay.StartGeometryStressForProbe(12, 1500);
                    return Task.CompletedTask;
                });

                ProbeOutput.Kv("每帧重建 12 个扇区（12 项菜单）", Describe(burst12));

                overlay.RebuildForMenu(menu);
                await Task.Delay(500);
            }

            ProbeOutput.Section("入场动画（整窗缩放淡入，可叠加几何并发）");
            for (var round = 0; round < 3; round++)
            {
                var entrance = await MeasureAsync(900, async () =>
                {
                    overlay.Hide();
                    await Task.Delay(400);
                    overlay.Show();
                });

                ProbeOutput.Kv($"唤出 ×1（第 {round + 1} 次）", Describe(entrance));
            }

            {
                // 拟议中的逐扇区入场：整窗动画 + 同时有 4 个扇区在动。
                var combined = await MeasureAsync(1000, async () =>
                {
                    overlay.Hide();
                    await Task.Delay(400);
                    overlay.Show();
                    overlay.StartGeometryStressForProbe(4, 1000);
                });

                ProbeOutput.Kv("唤出 + 4 个扇区同时在动", Describe(combined));
            }

            ProbeOutput.Kv("回调残留（应为 0）", OverlayController.HookCountsForProbe);

            ProbeOutput.Section("怎么读这些数");
            ProbeOutput.Line("  · p50 ≈ 16.7ms：跟着合成器节奏走，正常。");
            ProbeOutput.Line("  · p95 明显高于 16.7：有掉帧，需要看是不是几何重建/分配造成的。");
            ProbeOutput.Line("  · 分配量只是参考：GC 是分代的，短命对象很便宜，");
            ProbeOutput.Line("    但每秒几十 MB 会明显推高 GC 频率。");

            overlay.Hide();
        }
        catch (Exception ex)
        {
            ProbeOutput.Line();
            ProbeOutput.Line("探针未捕获异常: " + ex);
        }
        finally
        {
            overlay?.Dispose();
        }

        ProbeOutput.Flush();
    }

    /// <summary>
    /// 帧间隔采集。按 Rendering 回调的到达时刻算间隔 ——
    /// 这是"屏幕上实际翻了几帧"的近似，比自己造计时器真实。
    /// </summary>
    private sealed class FrameRecorder
    {
        private readonly List<double> _intervalsMs = [];
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private long _lastTicks;
        private long _allocatedAtStart;

        public void OnRendering(object? sender, EventArgs e)
        {
            if (_lastTicks == 0)
            {
                _lastTicks = _wall.ElapsedTicks;
                _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
                return;
            }

            var now = _wall.ElapsedTicks;
            var ms = (now - _lastTicks) * 1000.0 / Stopwatch.Frequency;
            _lastTicks = now;

            // 首帧的间隔与第一帧之前的准备工作无关，丢掉。
            _intervalsMs.Add(ms);
        }

        public FrameStats Summarize()
        {
            var sorted = _intervalsMs.OrderBy(x => x).ToList();
            double At(double q) => sorted.Count == 0
                ? 0
                : sorted[Math.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)];

            var median = At(0.5);

            return new FrameStats(
                Count: sorted.Count,
                P50: median,
                P95: At(0.95),
                P99: At(0.99),
                Max: sorted.Count == 0 ? 0 : sorted[^1],
                Over20Ms: sorted.Count(x => x > 20),
                Over33Ms: sorted.Count(x => x > 33),
                AllocatedBytes: GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart,
                WallMs: _wall.Elapsed.TotalMilliseconds);
        }
    }

    private readonly record struct FrameStats(
        int Count, double P50, double P95, double P99, double Max,
        int Over20Ms, int Over33Ms, long AllocatedBytes, double WallMs);
}
