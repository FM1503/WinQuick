using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WinQuick.App.Radial;
using WinQuick.Core.Geometry;
using WinQuick.Core.Motion;
using ShapePath = System.Windows.Shapes.Path;

namespace WinQuick.App.Radial;

/// <summary>
/// 悬停形变：被悬停的扇区向外"鼓出"一段。
///
/// 为什么用逐帧驱动几何，而不是给它挂一个 ScaleTransform：
/// 缩放是**以某个中心等比放大**，会把扇区内缘也一起推向圆心、让它离开中心死区。
/// 而"向外延伸"的正确形态是**内缘不动、外缘外扩** —— 扇区像抽屉一样被拉出来。
/// 这只能靠改几何实现（WPF 的几何不是可动画属性，也不支持对 Path 做非等比的径向变形）。
///
/// 代价是可控的：同一时刻最多只有两个扇区在变化（新悬停的鼓出、旧悬停的缩回），
/// 每个扇区每帧重建一次小几何；静止时整条渲染回调会断开，不产生任何逐帧开销。
/// </summary>
internal sealed class SectorBulgeAnimator
{
    private readonly ShapePath[] _sectors;
    private readonly PathGeometry[] _baseGeometry;
    private readonly Point _center;
    private readonly double _innerRadius;
    private readonly double _baseOuterRadius;
    private readonly SectorSpan[] _spans;
    private readonly double _gapDegrees;
    private readonly double _bulgePixels;
    private readonly CubicBezier _easing;
    private readonly double _durationMs;

    // 每个扇区各自的动画状态：当前值、起点值、起始时刻、目标值。
    private readonly double[] _current;
    private readonly double[] _from;
    private readonly double[] _target;
    private readonly double[] _startedAtMs;

    private int _hoverIndex = -1;
    private bool _running;

    public SectorBulgeAnimator(
        ShapePath[] sectors, PathGeometry[] baseGeometry, Point center,
        double innerRadius, double baseOuterRadius, SectorSpan[] spans, double gapDegrees,
        double bulgePixels, string easing, int durationMs)
    {
        _sectors = sectors;
        _baseGeometry = baseGeometry;
        _center = center;
        _innerRadius = innerRadius;
        _baseOuterRadius = baseOuterRadius;
        _spans = spans;
        _gapDegrees = gapDegrees;
        _bulgePixels = bulgePixels;
        _easing = CubicBezier.Parse(easing);
        _durationMs = Math.Max(1, durationMs);

        var n = sectors.Length;
        _current = new double[n];
        _from = new double[n];
        _target = new double[n];
        _startedAtMs = new double[n];
    }

    /// <summary>被悬停扇区当前的鼓出像素数 —— 供验证形变确实发生了。</summary>
    public double CurrentBulgeOf(int index) =>
        index >= 0 && index < _current.Length ? _current[index] * _bulgePixels : 0;

    /// <summary>是否正在动画中。</summary>
    public bool IsAnimating => _running;

    public void SetHover(int index)
    {
        if (index == _hoverIndex) return;
        _hoverIndex = index;

        var now = NowMs();
        for (var i = 0; i < _current.Length; i++)
        {
            var goal = i == index ? 1.0 : 0.0;
            if (Math.Abs(_target[i] - goal) < 1e-6 && _running) continue;

            // 从"当前值"重新起步，而不是从 0 或 1 —— 快速划过多个扇区时不会跳变。
            _from[i] = _current[i];
            _target[i] = goal;
            _startedAtMs[i] = now;
        }

        Start();
    }

    /// <summary>
    /// 立即把悬停形变落定到终态，不播放动画。
    /// 离屏渲染需要它 —— RenderPreview 只渲染一帧，动画还没推进，
    /// 否则画出来的永远是未形变的状态，形变就等于没被验证过。
    /// </summary>
    public void SetHoverImmediate(int index)
    {
        _hoverIndex = index;

        for (var i = 0; i < _current.Length; i++)
        {
            _target[i] = i == index ? 1.0 : 0.0;
            _current[i] = _target[i];
            _from[i] = _target[i];
            ApplyBulge(i);
        }

        Stop();
    }

    private static double NowMs() => Environment.TickCount64;

    /// <summary>
    /// 当前挂着的逐帧回调数。
    ///
    /// 为什么需要它：<c>CompositionTarget.Rendering</c> 是**静态**事件，
    /// 一旦哪个动画器在动画进行中就被丢弃（换菜单会整棵换掉视觉树），
    /// 它的订阅会一直留在静态事件上 —— 于是这个动画器、它引用的扇区、
    /// 乃至整棵旧视觉树都不会被回收，而且每一帧都还在跑。
    /// 这种泄漏在性能劣化到肉眼可见之前完全没有症状，只能靠计数抓。
    /// </summary>
    public static int ActiveHookCount => Volatile.Read(ref _activeHooks);

    private static int _activeHooks;

    private void Start()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
        Interlocked.Increment(ref _activeHooks);
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        Interlocked.Decrement(ref _activeHooks);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = NowMs();
        var anyActive = false;

        for (var i = 0; i < _current.Length; i++)
        {
            if (Math.Abs(_current[i] - _target[i]) < 1e-4) continue;

            var t = (now - _startedAtMs[i]) / _durationMs;
            if (t >= 1.0)
            {
                _current[i] = _target[i];
            }
            else
            {
                anyActive = true;
                _current[i] = _from[i] + (_target[i] - _from[i]) * _easing.Ease(t);
            }

            ApplyBulge(i);
        }

        if (!anyActive)
        {
            // 全部落定：把几何还原成冻结的基准几何，然后断开渲染回调。
            for (var i = 0; i < _current.Length; i++)
                if (Math.Abs(_current[i]) < 1e-6) _sectors[i].Data = _baseGeometry[i];

            Stop();
        }
    }

    private void ApplyBulge(int index)
    {
        var bulge = _current[index] * _bulgePixels;
        if (bulge < 0.05)
        {
            _sectors[index].Data = _baseGeometry[index];
            return;
        }

        var span = _spans[index];
        _sectors[index].Data = SectorGeometry.RingSector(
            _center, _innerRadius, _baseOuterRadius + bulge,
            span.StartDeg + _gapDegrees, span.EndDeg - _gapDegrees);
    }

    /// <summary>
    /// 立即把所有扇区复位到基准几何并停止动画。
    /// 覆盖层隐藏时必须调用 —— 否则残留的渲染回调会在窗口不可见时继续跑。
    /// </summary>
    public void Reset()
    {
        Stop();
        _hoverIndex = -1;

        for (var i = 0; i < _current.Length; i++)
        {
            _current[i] = 0;
            _target[i] = 0;
            _sectors[i].Data = _baseGeometry[i];
        }
    }
}
