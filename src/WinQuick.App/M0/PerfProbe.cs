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
