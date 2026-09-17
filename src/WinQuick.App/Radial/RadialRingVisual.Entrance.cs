using WinQuick.Core.Motion;

namespace WinQuick.App.Radial;

/// <summary>
/// 逐格入场：从 12 点那格（索引 0）起，按索引顺序顺时针一格格鼓出来。
///
/// **顺序为什么就是索引顺序**：<c>Vec2.FromPolar</c> 是 <c>(r·sinθ, −r·cosθ)</c>，
/// θ=0 就是 12 点方向、θ 增大就是屏幕上的顺时针；而 <c>RadialLayout.Distribute</c>
/// 让索引 0 居中在 12 点。所以"从 12 点顺时针逐个"＝按数组顺序 ——
/// 不需要任何额外的角度排序。附带的好处：用户拖拽调整过的顺序，就是入场顺序。
///
/// **几何只有一个写入点**（<see cref="WriteRingSectorGeometry"/>）：入场与悬停鼓出
/// 是两条独立的逐帧回调，谁都能在任何一帧动同一个扇区 —— 各写各的，后写的就把前一个
/// 抹掉了。子环那边是同一个坑，见 <c>WriteChildSectorGeometry</c> 的说明。
/// </summary>
public sealed partial class RadialRingView
{
    /// <summary>每格当前的入场进度（静止时是空数组，<see cref="RevealOf"/> 一律返回 1）。</summary>
    private double[] _reveal = [];

    private StaggerRevealAnimator? _entrance;
    private double _centerHintReveal = 1;

    /// <summary>
    /// 入场前图标、文字、以及分组箭头各自的透明度。**记录而不是套常量**：静止态的那个值
    /// 依赖历史 —— 刚建好的环图标是 1.0，悬停过一次之后就变成 0.94（ApplySectorStyle 里
    /// 那个非悬停值）。套常量必然在两种历史下错一种，表现是"入场结束后比以前亮/暗一点点"。
    /// </summary>
    private double[] _iconOpacityBefore = [];
    private double[] _labelOpacityBefore = [];
    private double[] _markerOpacityBefore = [];

    /// <summary>诊断用：某格当前的入场进度（0 = 还没长出来，1 = 已落定）。</summary>
    internal double EntranceRevealOf(int index) => RevealOf(index);

    internal bool IsEntranceAnimating => _entrance?.IsAnimating ?? false;

    /// <summary>诊断用：某格的起始延迟 —— 用来核对"从 12 点那格开始、按索引递增"。</summary>
    internal double EntranceStartDelayOf(int index) => _entrance?.StartDelayOf(index) ?? 0;

    internal double CenterHintReveal => _centerHintReveal;

    internal static int ActiveEntranceHookCount => StaggerRevealAnimator.ActiveHookCount;

    private double RevealOf(int index) =>
        index >= 0 && index < _reveal.Length ? _reveal[index] : 1.0;

    /// <summary>
    /// 首态（"残影"）映射：把动画器的原始进度翻成**实际画出来的完整度**。
    ///
    /// 原始进度 0（＝刚轮到它）不是"什么都没有"，而是用户指定的首态：
    /// 25% 厚度 + 50% 不透明度；原始进度 1 才是完整实态。
    /// 这样做的好处：每格不必从零长起 —— 它一出现就已经"存在"了，
    /// 于是格距可以压到很小、整轮因此短得多，而"新出现一格"仍是可读的离散事件。
    /// 原始进度小于 0（还没轮到）才是不显示。
    /// </summary>
    private double ApplyGhostReveal(double raw) =>
        StaggerReveal.ApplyGhost(raw, _tokens.Motion.SectorGhostReveal);

    private double ApplyGhostOpacity(double raw) =>
        StaggerReveal.ApplyGhost(raw, _tokens.Motion.SectorGhostOpacity);

    /// <summary>
    /// 把入场起始状态落定：全部扇区零厚度、图标文字与中心提示全透明。
    /// **必须在 <c>Show()</c> 之前调用** —— 否则会先以终态渲染一两帧再开始长，
    /// 看起来是"先闪一下整个圆盘"。这条在设置面板上出过一次事故。
    /// </summary>
    internal void PrepareEntrance()
    {
        StopEntrance();

        // 全部先"不在"（-1）：不是残影首态 —— 还没轮到的格不该出现在盘面上。
        _reveal = new double[_sectors.Length];
        for (var i = 0; i < _reveal.Length; i++) _reveal[i] = StaggerRevealAnimator.NotStarted;
        for (var i = 0; i < _sectors.Length; i++) WriteRingSectorGeometry(i, 0);

        // **只在还没有记录过的时候记录。**
        // 休眠时也会调用本方法（让停在屏幕外的窗口里装着"空的环"），
        // 若无条件记录，第二次会把"刚被清零的值"记成原值 —— 于是入场结束后
        // 图标与文字会保持全透明。记录一直保留到 RestoreRestingVisuals 收尾。
        if (_iconOpacityBefore.Length != _icons.Length)
        {
            _iconOpacityBefore = _icons.Select(icon => icon.Opacity).ToArray();
            _labelOpacityBefore = _labels.Select(label => label.Opacity).ToArray();
            _markerOpacityBefore = _childMarkers
                .Select(marker => marker?.Opacity ?? 0)
                .ToArray();
        }

        for (var i = 0; i < _icons.Length; i++) _icons[i].Opacity = 0;
        for (var i = 0; i < _labels.Length; i++) _labels[i].SetOpacity(0);
        for (var i = 0; i < _childMarkers.Length; i++)
        {
            // **分组箭头必须跟着自己那一格出现。** 漏掉它的表现是：圆盘还在逐格长，
            // 有子项的扇区上那支箭头却已经全亮着 —— 用户一眼就看出来了。
            if (_childMarkers[i] is { } marker) marker.Opacity = 0;
        }

        _escHint.Opacity = 0;
        _centerHintReveal = 0;
    }

    /// <summary>播放逐格入场。起始状态由动画器自己再落定一次，所以重复调用是安全的。</summary>
    internal void PlaySequentialEntrance()
    {
        var motion = _tokens.Motion;
        var plan = StaggerReveal.Plan(
            _sectors.Length, motion.SectorStaggerMs, motion.SectorPopMs, motion.SectorSweepMaxMs);

        StopEntrance();
        if (_reveal.Length != _sectors.Length) _reveal = new double[_sectors.Length];

        _entrance = new StaggerRevealAnimator(
            _sectors.Length, plan.StaggerMs, motion.SectorPopMs, motion.SectorPopEasing,
            write: WriteEntranceStep,
            onFrame: UpdateCenterHint,
            onCompleted: FinishEntrance);

        _entrance.Start();
    }

    /// <summary>立即落定终态：不动画、直接是完成后的样子。</summary>
    internal void CompleteEntranceImmediate()
    {
        StopEntrance();

        _reveal = [];
        RestoreRestingVisuals();

        _iconOpacityBefore = [];
        _labelOpacityBefore = [];
        _markerOpacityBefore = [];

        _centerHintReveal = 1;
        _escHint.Opacity = 1;
    }

    private void StopEntrance()
    {
        _entrance?.Stop();
        _entrance = null;
    }

    private void WriteEntranceStep(int index, double progress)
    {
        if (index < 0 || index >= _reveal.Length) return;

        // 存**实际完整度**（残影已计入）：探针读它就是"这格现在有多完整"。
        var reveal = ApplyGhostReveal(progress);
        _reveal[index] = reveal;

        // 几何走统一写入点（与悬停鼓出合成）。
        WriteRingSectorGeometry(index, 0);

        // 图标与文字跟着自己那一格到。透明度必须夹住：缓动带一点回弹，
        // 不夹的话会算出大于 1 的透明度。
        var opacity = ApplyGhostOpacity(progress);
        if (index < _icons.Length) _icons[index].Opacity = opacity * OriginalIconOpacity(index);
        if (index < _labels.Length) _labels[index].SetOpacity(opacity * OriginalLabelOpacity(index));

        if (index < _childMarkers.Length && _childMarkers[index] is { } marker)
            marker.Opacity = opacity * OriginalMarkerOpacity(index);
    }

    /// <summary>中心提示是交互锚点，要在最前面淡入，不参与逐格序列。</summary>
    private void UpdateCenterHint(double elapsedMs)
    {
        var ms = Math.Max(1, _tokens.Motion.CenterHintMs);
        var value = Math.Clamp(elapsedMs / ms, 0, 1);
        if (Math.Abs(value - _centerHintReveal) < 1e-3) return;

        _centerHintReveal = value;
        _escHint.Opacity = value;
    }

    private void FinishEntrance()
    {
        _reveal = [];
        RestoreRestingVisuals();
    }

    /// <summary>诊断用：某个扇区分组箭头当前的透明度（-1 = 该项没有子项）。</summary>
    internal double MarkerOpacityForProbe(int index) =>
        index >= 0 && index < _childMarkers.Length && _childMarkers[index] is { } marker
            ? marker.Opacity
            : -1;

    /// <summary>诊断用：入场前的图标透明度快照（核对"原样恢复"）。</summary>
    internal double IconOpacityBeforeEntrance(int index) => OriginalIconOpacity(index);

    /// <summary>
    /// 回到静止态：几何用冻结的基准几何（逐像素可复现），图标与文字恢复**入场前记录的值**。
    /// 入场结束、隐藏复位、离屏渲染都走这里 —— 只有一处，才不会漏掉某个属性。
    /// </summary>
    private void RestoreRestingVisuals()
    {
        for (var i = 0; i < _sectors.Length; i++) WriteRingSectorGeometry(i, 0);

        for (var i = 0; i < _icons.Length; i++)
            _icons[i].Opacity = OriginalIconOpacity(i);

        for (var i = 0; i < _labels.Length; i++)
            _labels[i].SetOpacity(OriginalLabelOpacity(i));

        for (var i = 0; i < _childMarkers.Length; i++)
        {
            if (_childMarkers[i] is { } marker) marker.Opacity = OriginalMarkerOpacity(i);
        }
    }

    private double OriginalMarkerOpacity(int index) =>
        _markerOpacityBefore.Length == _childMarkers.Length ? _markerOpacityBefore[index] : 1.0;

    private double OriginalIconOpacity(int index) =>
        _iconOpacityBefore.Length == _icons.Length ? _iconOpacityBefore[index] : _icons[index].Opacity;

    private double OriginalLabelOpacity(int index) =>
        _labelOpacityBefore.Length == _labels.Length ? _labelOpacityBefore[index] : 1.0;
}
