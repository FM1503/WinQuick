using System.Windows.Media;
using WinQuick.Core.Motion;

namespace WinQuick.App.Radial;

/// <summary>
/// 子弧的展开 / 收起动画。
///
/// 形态：子扇区从内缘"长"到外缘 —— 起始时内外半径重合（退化成一个没有厚度的弧），
/// 结束时到达完整环带。这比单纯淡入更像"从父扇区方向生长出来"，
/// 也就是 Fluent 的 Connected 原则要求的视觉连贯。
///
/// 与悬停形变一样用逐帧驱动几何：WPF 的几何不是可动画属性，也不支持非等比的径向变形。
/// 动画结束后立刻断开渲染回调，静止时不产生任何逐帧开销。
/// </summary>
internal sealed class ChildArcAnimator
{
    private readonly double _durationMs;
    private readonly CubicBezier _easing;
    private readonly Action<double> _onProgress;

    private bool _running;
    private double _from;
    private double _target;
    private double _current;
    private double _startedAtMs;

    /// <param name="onProgress">每帧回调，参数是 0（完全收起）到 1（完全展开）的进度。</param>
    public ChildArcAnimator(int durationMs, string easing, Action<double> onProgress)
    {
        _durationMs = Math.Max(1, durationMs);
        _easing = CubicBezier.Parse(easing);
        _onProgress = onProgress;
    }

    /// <summary>当前进度：0 = 完全收起，1 = 完全展开。供验证用。</summary>
    public double Progress => _current;

    public bool IsAnimating => _running;

    public void Expand()
    {
        if (_running && Math.Abs(_target - 1.0) < 1e-6) return;
        StartTowards(1.0);
    }

    public void Collapse()
    {
        if (Math.Abs(_current) < 1e-6 && !_running) return;
        StartTowards(0.0);
    }

    /// <summary>立即落定到展开态，不播放动画。供离屏渲染使用。</summary>
    public void SetImmediate(double progress)
    {
        Stop();
        _current = _from = _target = progress;
        _onProgress(_current);
    }

    private void StartTowards(double target)
    {
        _from = _current;
        _target = target;
        _startedAtMs = Environment.TickCount64;

        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = (Environment.TickCount64 - _startedAtMs) / _durationMs;

        if (t >= 1.0)
        {
            _current = _target;
            _onProgress(_current);
            Stop();
            return;
        }

        _current = _from + (_target - _from) * _easing.Ease(t);
        _onProgress(_current);
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>停止并复位。覆盖层隐藏时必须调用，否则回调会在窗口不可见时继续跑。</summary>
    public void Reset()
    {
        Stop();
        _current = _from = _target = 0;
    }
}
