using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;
using WinQuick.Core.Design;

namespace WinQuick.App.M0;

/// <summary>
/// 粒子动画探针 —— 回答"在这个工具里做粒子效果，代价是多少"。
///
/// 为什么必须先量而不是先选方案：轮盘窗口是**分层窗口**（`AllowsTransparency=true`），
/// 它的呈现要走一次额外的表面拷贝，代价与窗口面积相关。而粒子动画天生是逐帧的 ——
/// 两者叠在一起正是最贵的组合。M0 的实测结论是"分层窗口的代价与面积×刷新率成正比"，
/// 所以"能不能做粒子"完全取决于这个数，而它只能量出来。
///
/// 量两种实现方式，因为它们的代价结构不同：
///
/// 1. **多个图形对象**（每颗粒子一个 Ellipse）：WPF 保留模式，每帧改位置即可，
///    代码最简单，但每颗粒子都要参与命中测试/布局/渲染遍历。
/// 2. **一个 WriteableBitmap**：每帧在内存里画点、按脏矩形写回。
///    图形对象只有一张，但代价落在逐像素的 CPU 写入上。
///
/// 同时量**基线**（没有粒子）—— 不然分不清代价是粒子带来的还是分层窗口本来就有的。
///
/// 用法：--particle-probe [--seconds=3]
/// </summary>
public static class ParticleProbe
{
    /// <summary>采样时逐个尝试的粒子数。</summary>
    private static readonly int[] Counts = [0, 100, 300, 800];

    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("particle", "m5");
        ProbeOutput.Line("量粒子动画在分层窗口上的代价：帧间隔分布 + 每帧分配。");
        ProbeOutput.Line("基线（0 颗粒子）一并量 —— 不然分不清代价是粒子带来的还是窗口本来就有的。");
        ProbeOutput.Line();

        var seconds = cli.GetInt("seconds", 3);
        var tokens = TokenStore.Current;

        OverlayController? overlay = null;
        try
        {
            overlay = new OverlayController(DefaultMenu.Build());
            overlay.Prewarm();
            overlay.Show();
            await Task.Delay(700);

            ProbeOutput.Section("环境");
            ProbeOutput.Kv("窗口边长", tokens.Radius.WindowSize + " DIP");
            ProbeOutput.Kv("每档采样时长", seconds + " 秒");
            ProbeOutput.Line();

            ProbeOutput.Section("A) 每颗粒子一个图形对象（Ellipse）");
            ProbeOutput.Kv("粒子数", "p50 / p95 / 最大（ms）│ 每秒分配");
            foreach (var n in Counts)
            {
                var stats = await MeasureAsync(overlay, new EllipseField(n), seconds);
                Report(n, stats);
            }

            ProbeOutput.Line();
            ProbeOutput.Section("B) 一张 WriteableBitmap + 脏矩形");
            ProbeOutput.Kv("粒子数", "p50 / p95 / 最大（ms）│ 每秒分配");
            foreach (var n in Counts)
            {
                var stats = await MeasureAsync(overlay, new BitmapField(n), seconds);
                Report(n, stats);
            }

            ProbeOutput.Line();
            ProbeOutput.Section("怎么读这些数");
            ProbeOutput.Line("  · 与「0（基线）」那一行比：差值就是粒子带来的成本。");
            ProbeOutput.Line("  · 本机 120Hz，理想帧间隔 8.33ms；p95 明显高于它就说明这一档带不动。");
            ProbeOutput.Line("  · 分配量是关键：图形对象方案每帧几乎不分配，");
            ProbeOutput.Line("    WriteableBitmap 方案每帧要写一遍像素缓冲 —— 两者代价位置不同。");

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

    private static void Report(int n, FrameStats s) =>
        ProbeOutput.Kv(n == 0 ? "  0（基线）" : "  " + n,
            $"{s.P50:0.00} / {s.P95:0.00} / {s.Max:0.00} │ {s.PerSecondKb:0} KB/s");

    private static async Task<FrameStats> MeasureAsync(
        OverlayController overlay, IParticleField field, int seconds)
    {
        overlay.AttachParticleFieldForProbe(field);

        // 先跑几帧让几何/缓冲就绪，再开始采样 —— 首帧包含创建成本，会污染分布。
        await Task.Delay(400);

        var recorder = new FrameRecorder();
        CompositionTarget.Rendering += recorder.OnRendering;

        await Task.Delay(seconds * 1000);

        CompositionTarget.Rendering -= recorder.OnRendering;
        overlay.AttachParticleFieldForProbe(null);

        return recorder.Summarize(seconds);
    }

    private readonly record struct FrameStats(
        double P50, double P95, double Max, double PerSecondKb);

    /// <summary>
    /// 帧间隔采集。与 --perf-probe 同一套口径：按 Rendering 回调的到达时刻算间隔，
    /// 测的是端到端观感而不是某项操作的纯耗时。
    /// </summary>
    private sealed class FrameRecorder
    {
        private readonly List<double> _intervals = [];
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
            _intervals.Add((now - _lastTicks) * 1000.0 / Stopwatch.Frequency);
            _lastTicks = now;
        }

        public FrameStats Summarize(int seconds)
        {
            var sorted = _intervals.OrderBy(x => x).ToList();
            double At(double q) => sorted.Count == 0
                ? 0
                : sorted[Math.Clamp((int)Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)];

            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart;

            return new FrameStats(
                At(0.5), At(0.95), sorted.Count == 0 ? 0 : sorted[^1],
                allocated / 1024.0 / Math.Max(0.001, seconds));
        }
    }

    /// <summary>一类粒子的实现：自己负责把视觉挂上去、逐帧推进、拆下来。</summary>
    private interface IParticleField
    {
        void Attach(Canvas canvas, Point center, double extent);
        void Detach();
        void Step();
    }

    /// <summary>每颗粒子一个 Ellipse。图形对象都挂在画布上，每帧只改位置。</summary>
    private sealed class EllipseField : IParticleField
    {
        private readonly int _count;
        private readonly List<System.Windows.Shapes.Ellipse> _dots = [];
        private readonly double[] _x = [];
        private readonly double[] _y = [];
        private readonly double[] _vx = [];
        private readonly double[] _vy = [];
        private Canvas? _canvas;
        private double _extent;
        private double _originX;
        private double _originY;

        public EllipseField(int count)
        {
            _count = count;
            _x = new double[count];
            _y = new double[count];
            _vx = new double[count];
            _vy = new double[count];
        }

        public void Attach(Canvas canvas, Point center, double extent)
        {
            _canvas = canvas;
            _extent = extent;
            _originX = center.X - extent / 2;
            _originY = center.Y - extent / 2;

            var rnd = new Random(20260915);
            var brush = TokenBrushes.Brush(TokenStore.Current.Color.HoverStroke, 0.75);
            brush.Freeze();

            for (var i = 0; i < _count; i++)
            {
                _x[i] = rnd.NextDouble() * extent;
                _y[i] = rnd.NextDouble() * extent;
                _vx[i] = (rnd.NextDouble() - 0.5) * 2.4;
                _vy[i] = (rnd.NextDouble() - 0.5) * 2.4;

                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = brush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, _originX + _x[i]);
                Canvas.SetTop(dot, _originY + _y[i]);
                canvas.Children.Add(dot);
                _dots.Add(dot);
            }

            CompositionTarget.Rendering += OnRendering;
        }

        public void Detach()
        {
            CompositionTarget.Rendering -= OnRendering;

            if (_canvas is not null)
                foreach (var dot in _dots) _canvas.Children.Remove(dot);

            _dots.Clear();
            _canvas = null;
        }

        private void OnRendering(object? sender, EventArgs e) => Step();

        public void Step()
        {
            for (var i = 0; i < _dots.Count; i++)
            {
                _x[i] += _vx[i] * 0.6;
                _y[i] += _vy[i] * 0.6;

                // 在给定范围内反弹。范围刻意限制在一小块区域：
                // 粒子铺满整个窗口时呈现代价会随面积上升，那是另一回事。
                if (_x[i] < 0 || _x[i] > _extent) _vx[i] = -_vx[i];
                if (_y[i] < 0 || _y[i] > _extent) _vy[i] = -_vy[i];

                Canvas.SetLeft(_dots[i], _originX + _x[i]);
                Canvas.SetTop(_dots[i], _originY + _y[i]);
            }
        }
    }

    /// <summary>一张 WriteableBitmap，每帧在缓冲区里画点、按脏矩形写回。</summary>
    private sealed class BitmapField : IParticleField
    {
        private const int Size = 360;          // DIP
        private readonly int _count;
        private readonly double[] _x = [];
        private readonly double[] _y = [];
        private readonly double[] _vx = [];
        private readonly double[] _vy = [];
        private readonly int[] _buffer = new int[Size * Size];
        private WriteableBitmap? _bitmap;
        private Image? _image;
        private Canvas? _canvas;
        private double _originX;
        private double _originY;

        public BitmapField(int count)
        {
            _count = count;
            _x = new double[count];
            _y = new double[count];
            _vx = new double[count];
            _vy = new double[count];
        }

        public void Attach(Canvas canvas, Point center, double extent)
        {
            _canvas = canvas;
            _originX = center.X - Size / 2.0;
            _originY = center.Y - Size / 2.0;

            _bitmap = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32, null);
            _image = new Image { Width = Size, Height = Size, Source = _bitmap, IsHitTestVisible = false };
            Canvas.SetLeft(_image, _originX);
            Canvas.SetTop(_image, _originY);
            canvas.Children.Add(_image);

            var rnd = new Random(20260915);
            for (var i = 0; i < _count; i++)
            {
                _x[i] = rnd.NextDouble() * Size;
                _y[i] = rnd.NextDouble() * Size;
                _vx[i] = (rnd.NextDouble() - 0.5) * 2.4;
                _vy[i] = (rnd.NextDouble() - 0.5) * 2.4;
            }

            CompositionTarget.Rendering += OnRendering;
        }

        public void Detach()
        {
            CompositionTarget.Rendering -= OnRendering;

            if (_canvas is not null && _image is not null) _canvas.Children.Remove(_image);

            _image = null;
            _bitmap = null;
            _canvas = null;
        }

        private void OnRendering(object? sender, EventArgs e) => Step();

        public void Step()
        {
            if (_bitmap is null) return;

            Array.Clear(_buffer);

            for (var i = 0; i < _count; i++)
            {
                _x[i] += _vx[i] * 0.6;
                _y[i] += _vy[i] * 0.6;
                if (_x[i] < 0 || _x[i] > Size) _vx[i] = -_vx[i];
                if (_y[i] < 0 || _y[i] > Size) _vy[i] = -_vy[i];

                PutDot((int)_x[i], (int)_y[i]);
            }

            _bitmap.WritePixels(
                new Int32Rect(0, 0, Size, Size), _buffer, Size * 4, 0);
        }

        /// <summary>在缓冲区里画一个 2x2 的点（预乘 alpha 的 Pbgra32）。</summary>
        private void PutDot(int cx, int cy)
        {
            for (var dy = 0; dy < 2; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= Size) continue;
                var row = y * Size;

                for (var dx = 0; dx < 2; dx++)
                {
                    var x = cx + dx;
                    if (x < 0 || x >= Size) continue;
                    _buffer[row + x] = unchecked((int)0xFFC8C8C8);   // 白，不透明
                }
            }
        }
    }
}
