using System.Windows.Media;
using WinQuick.Core.Motion;

namespace WinQuick.App.Radial;

/// <summary>
/// 逐项依次出现的驱动：第 i 项在自己的时刻开始，用同一段时长长到满。
///
/// 为什么不用 WPF 的动画系统：几何不是可动画属性（"鼓出"要改路径数据），
/// 而 `BeginTime` 只能错开 WPF 自己的属性动画 —— 所以只能逐帧自己驱动，
/// 与悬停形变、子弧展开同一套路：结束后立刻断开回调，静止时不产生逐帧开销。
///
/// 两处用它：轮盘入场（索引顺序＝从 12 点顺时针）与子环逐项长出。
///
/// 缓动允许 y&gt;1 —— 那就是"回弹"：外缘会先鼓过头再收回来。
/// 结束后写的是**精确的 1**（不是 Ease(1) 的浮点近似），
/// 这样"动画结束后与静止态逐像素相同"这条不变量才守得住（--perf-probe 锁着它）。
/// </summary>
internal sealed class StaggerRevealAnimator
{
    private readonly int _count;
    private readonly Action<int, double> _write;
    private readonly Action? _onCompleted;
    private readonly Action<double>? _onFrame;
    private readonly CubicBezier _easing;
    private readonly double _itemMs;
    private readonly double _staggerMs;
    private readonly double[] _progress;

    private long _startedAtMs;
    private bool _running;

    private static int _hooks;

    /// <summary>挂着的逐帧回调数。与其它动画器共用一个约定 —— 残留会让整棵旧视觉树既不被回收、又每帧都在跑。</summary>
    internal static int ActiveHookCount => Volatile.Read(ref _hooks);

    /// <param name="write">每项进度变化时的回调（进度已加缓动，可能 &gt;1）。</param>
    /// <param name="onFrame">每帧回调（已过去的毫秒数）。给"不在逐项序列里、但也要跟着动"的元素用（中心提示）。</param>
    /// <param name="onCompleted">整轮结束时回调一次。</param>
    public StaggerRevealAnimator(
        int count, double staggerMs, double itemMs, string easing,
        Action<int, double> write, Action<double>? onFrame = null, Action? onCompleted = null)
    {
        _count = Math.Max(0, count);
        _staggerMs = Math.Max(0, staggerMs);
        _itemMs = Math.Max(1, itemMs);
        _easing = CubicBezier.Parse(easing);
        _write = write;
        _onFrame = onFrame;
        _onCompleted = onCompleted;
        _progress = new double[_count];
    }

    public bool IsAnimating => _running;

    public double TotalMs => _staggerMs * Math.Max(0, _count - 1) + _itemMs;

    /// <summary>第 i 项的起始延迟（探针用它核对"从 12 点那格开始、按索引递增"）。</summary>
    public double StartDelayOf(int index) => index * _staggerMs;

    public double ProgressOf(int index) =>
        index >= 0 && index < _count ? Math.Max(0, _progress[index]) : 1.0;

    public double ElapsedMs => _running ? Environment.TickCount64 - _startedAtMs : TotalMs;

    /// <summary>
    /// "还没轮到"的哨兵值。视图把它翻译成"不显示"（零厚度、全透明），
    /// 与"轮到但进度为 0"（＝首态，见视图里的残影映射）区分开 ——
    /// 少了这个区分，没轮到的格会以首态提前出现在盘面上。
    /// </summary>
    public const double NotStarted = -1;

    public void Start()
    {
        // 起始状态由动画器自己落定：调用方不必记得先清一遍（漏了就会先闪一帧满环）。
        for (var i = 0; i < _count; i++)
        {
            _progress[i] = NotStarted;
            _write(i, NotStarted);
        }

        _startedAtMs = Environment.TickCount64;
        _onFrame?.Invoke(0);

        if (_running || _count == 0)
        {
            if (_count == 0) _onCompleted?.Invoke();
            return;
        }

        _running = true;
        CompositionTarget.Rendering += OnRendering;
        Interlocked.Increment(ref _hooks);
    }

    /// <summary>立即落定终态（"关掉系统动画"、离屏渲染、隐藏复位都走它）。</summary>
    public void Complete()
    {
        Stop();

        for (var i = 0; i < _count; i++)
        {
            _progress[i] = 1;
            _write(i, 1);
        }

        _onFrame?.Invoke(TotalMs);
        _onCompleted?.Invoke();
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
        Interlocked.Decrement(ref _hooks);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var elapsed = Environment.TickCount64 - _startedAtMs;
        _onFrame?.Invoke(elapsed);

        var allDone = true;

        for (var i = 0; i < _count; i++)
        {
            // 还没轮到它的格保持"不在"：既不动、也不写。
            if (elapsed < StartDelayOf(i)) continue;

            var raw = StaggerReveal.RawProgress(i, elapsed, _staggerMs, _itemMs);

            if (raw >= 1)
            {
                // 落定写**精确的 1**，不走缓动 —— 回弹曲线在尾部的浮点值不等于 1，
                // 用它会让几何与静止态差一丁点，而"逐像素相同"的核对正是靠这里守住。
                if (_progress[i] < 1)
                {
                    _progress[i] = 1;
                    _write(i, 1);
                }

                continue;
            }

            allDone = false;

            var eased = _easing.Ease(raw);
            if (Math.Abs(eased - _progress[i]) < 1e-4) continue;

            _progress[i] = eased;
            _write(i, eased);
        }

        if (!allDone) return;

        Stop();
        _onCompleted?.Invoke();
    }
}
