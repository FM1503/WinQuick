using System.Windows.Media;
using WinQuick.Core.Geometry;

namespace WinQuick.App.Radial;

/// <summary>
/// 只给 <c>--perf-probe</c> 用的**几何并发压力**。
///
/// 要回答的问题：入场若改成"逐个扇区鼓出"，同一时刻会有好几个扇区在改几何 ——
/// 这笔开销在分层窗口上到底占多少？它决定节奏参数能放开到什么程度
/// （交错间隔 × 每个扇区时长 → 同时在动的扇区数）。
///
/// 为什么不"先把入场实现出来再量"：入场一旦写进产品代码，就会把一串**设计决定**
/// （交错多少毫秒、从哪儿开始、图标跟不跟着淡入）一起带进来；而
/// "每帧重建 N 个扇区的几何要不要钱"与设计无关，可以先单独回答。
///
/// 与产品动画的唯一区别：这里由探针直接驱动，并且**必然主动断开**回调。
/// 回调计数（<see cref="ActiveStressHookCount"/>）与动画器共用一个约定 ——
/// 残留的静态订阅会让整棵旧视觉树既不被回收、又每帧都在跑。
/// </summary>
public sealed partial class RadialRingView
{
    private int _stressSectors;
    private bool _stressRunning;
    private long _stressStartedMs;
    private int _stressDurationMs = 1500;

    private static int _stressHooks;

    /// <summary>压力是否在跑。</summary>
    internal bool IsGeometryStressRunning => _stressRunning;

    /// <summary>压力中每帧重建几何的扇区数。</summary>
    internal int GeometryStressSectors => _stressSectors;

    /// <summary>挂着的压力回调数。归零才算干净。</summary>
    internal static int ActiveStressHookCount => Volatile.Read(ref _stressHooks);

    /// <summary>
    /// 让前 <paramref name="sectorCount"/> 个扇区连续重建几何。
    /// 形态贴着"鼓出"：外缘在环带内外之间往复 —— 与逐扇区入场是同一类几何改动。
    /// </summary>
    internal void StartGeometryStressForProbe(int sectorCount, int durationMs = 1500)
    {
        StopGeometryStressForProbe();

        _stressSectors = Math.Clamp(sectorCount, 0, _sectors.Length);
        if (_stressSectors == 0) return;

        _stressStartedMs = Environment.TickCount64;
        _stressDurationMs = Math.Max(1, durationMs);
        _stressRunning = true;

        CompositionTarget.Rendering += OnStressRendering;
        Interlocked.Increment(ref _stressHooks);
    }

    internal void StopGeometryStressForProbe()
    {
        if (!_stressRunning) return;

        _stressRunning = false;
        CompositionTarget.Rendering -= OnStressRendering;
        Interlocked.Decrement(ref _stressHooks);
        RestoreRestingGeometry();
    }

    private void OnStressRendering(object? sender, EventArgs e)
    {
        var elapsed = Environment.TickCount64 - _stressStartedMs;
        if (elapsed >= _stressDurationMs)
        {
            StopGeometryStressForProbe();
            return;
        }

        // 0 → 1 → 0：外缘在环带里往复两趟。
        var phase = (double)elapsed / _stressDurationMs;
        var wave = 0.5 - 0.5 * Math.Cos(phase * Math.PI * 4);

        var radius = _tokens.Radius;
        var outer = radius.DeadZone + (radius.Ring1Outer - radius.DeadZone) * (0.35 + 0.65 * wave);

        for (var i = 0; i < _stressSectors; i++) ApplyOuter(i, outer);
    }

    private void ApplyOuter(int index, double outer)
    {
        var span = _spans[index];
        _sectors[index].Data = SectorGeometry.RingSector(
            _center, _tokens.Radius.DeadZone, outer,
            span.StartDeg + _tokens.Radius.SectorGapDegrees,
            span.EndDeg - _tokens.Radius.SectorGapDegrees);
    }

    /// <summary>把全部扇区还原成静止几何 —— 压力结束后必须回到与静止态一致的样子。</summary>
    private void RestoreRestingGeometry()
    {
        for (var i = 0; i < _sectors.Length; i++) ApplyOuter(i, _tokens.Radius.Ring1Outer);
    }
}
