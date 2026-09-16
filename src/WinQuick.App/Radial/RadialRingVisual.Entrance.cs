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
    /// 入场前图标与文字各自的透明度。**记录而不是套常量**：静止态的那个值依赖历史 ——
    /// 刚建好的环图标是 1.0，悬停过一次之后就变成 0.94（ApplySectorStyle 里那个非悬停值）。
    /// 套常量必然在两种历史下错一种，表现是"入场结束后图标比以前亮/暗一点点"。
    /// </summary>
    private double[] _iconOpacityBefore = [];
    private double[] _labelOpacityBefore = [];

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
    /// 把入场起始状态落定：全部扇区零厚度、图标文字与中心提示全透明。
    /// **必须在 <c>Show()</c> 之前调用** —— 否则会先以终态渲染一两帧再开始长，
    /// 看起来是"先闪一下整个圆盘"。这条在设置面板上出过一次事故。
    /// </summary>
    internal void PrepareEntrance()
    {
        StopEntrance();

        _reveal = new double[_sectors.Length];
        for (var i = 0; i < _sectors.Length; i++) WriteRingSectorGeometry(i, 0);

        _iconOpacityBefore = _icons.Select(icon => icon.Opacity).ToArray();
        _labelOpacityBefore = _labels.Select(label => label.Opacity).ToArray();

        for (var i = 0; i < _icons.Length; i++) _icons[i].Opacity = 0;
        for (var i = 0; i < _labels.Length; i++) _labels[i].SetOpacity(0);

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

        _reveal[index] = progress;

        // 几何走统一写入点（与悬停鼓出合成）。
        WriteRingSectorGeometry(index, 0);

        // 图标与文字跟着自己那一格到。透明度必须夹住：缓动带约 6% 回弹，
        // 不夹的话会算出 1.06 的透明度。
        var opacity = Math.Clamp(progress, 0, 1);
        if (index < _icons.Length) _icons[index].Opacity = opacity * OriginalIconOpacity(index);
        if (index < _labels.Length) _labels[index].SetOpacity(opacity * OriginalLabelOpacity(index));
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
    }

    private double OriginalIconOpacity(int index) =>
        _iconOpacityBefore.Length == _icons.Length ? _iconOpacityBefore[index] : _icons[index].Opacity;

    private double OriginalLabelOpacity(int index) =>
        _labelOpacityBefore.Length == _labels.Length ? _labelOpacityBefore[index] : 1.0;
}
