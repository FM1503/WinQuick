using System.Windows.Media;
using WinQuick.Core.Motion;

namespace WinQuick.App.Radial;

/// <summary>
/// 层级位移的进度驱动 —— 「旧的环 2 整层搬到环 1」那一段的动画时钟。
///
/// 形态（用户从 preview/wheel2-preview.html 选定的 ②）：旧环 2 那一层按角度比例
/// **展开成整圈**，同时半径带从子环（218–350）收进环一（56–200）；整圈不旋转 ——
/// 父项留在它原来的朝向，子弧就从那个方向继续展开。落在屏上的感觉是"这一圈往里收了一圈"。
///
/// 为什么用逐帧而不是 Storyboard：WPF 的几何不是可动画属性，而且这一步不是等比的
/// 径向变形（内缘 218→56 与 外缘 350→200 的缩放系数不同：0.26 与 0.57），
/// 任何 MatrixTransform 都表达不了它。悬停鼓出与子弧展开当初也是同样的理由走的逐帧。
/// 动画一结束立刻断开回调，静止时不产生任何逐帧开销（静止态逐像素可复现那条不变量由 --perf-probe 锁着）。
/// </summary>
internal sealed class LevelShiftAnimator
{
    private readonly double _durationMs;
    private readonly CubicBezier _easing;
    private readonly Action<double> _onProgress;

    private bool _running;
    private long _startedAtMs;

    /// <param name="onProgress">每帧回调，参数是 0（还在旧环 2 的位置）到 1（已到环 1 的槽位）。</param>
    public LevelShiftAnimator(int durationMs, string easing, Action<double> onProgress)
    {
        _durationMs = Math.Max(1, durationMs);
        _easing = CubicBezier.Parse(easing);
        _onProgress = onProgress;
    }

    public bool IsAnimating => _running;

    /// <summary>
    /// 从旧环 2 的位置展开到环 1 的槽位。
    /// 时长为 0 时直接落到终点 —— 那一档等于"不做位移动画"，走同一条代码路径。
    /// </summary>
    public void Start(int durationOverrideMs = -1)
    {
        var duration = durationOverrideMs >= 0 ? durationOverrideMs : _durationMs;
        if (duration <= 0)
        {
            _onProgress(1);
            return;
        }

        _startedAtMs = Environment.TickCount64;
        if (_running) return;

        _running = true;
        CompositionTarget.Rendering += OnRendering;
        Interlocked.Increment(ref _activeHooks);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = (Environment.TickCount64 - _startedAtMs) / _durationMs;

        if (t >= 1.0)
        {
            _onProgress(1);
            Stop();
            return;
        }

        _onProgress(_easing.Ease(t));
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        Interlocked.Decrement(ref _activeHooks);
    }

    /// <summary>当前挂着的逐帧回调数。理由见 <see cref="SectorBulgeAnimator.ActiveHookCount"/>。</summary>
    public static int ActiveHookCount => Volatile.Read(ref _activeHooks);

    private static int _activeHooks;

    /// <summary>停止并复位。丢树/隐藏时必须调用，否则回调会在窗口不可见时继续跑。</summary>
    public void Reset()
    {
        Stop();
        _startedAtMs = 0;
    }
}
