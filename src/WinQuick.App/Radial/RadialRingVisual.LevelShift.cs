using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinQuick.App.Design;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using ShapePath = System.Windows.Shapes.Path;

namespace WinQuick.App.Radial;

/// <summary>
/// 层级位移：点环 2 的分组扇区进下一层时，旧环 2 那一层整层搬到环 1 的位置。
///
/// 这一段是"同时只看到两级"这个交互唯一需要的**新**动画：环 1 与环 2 的几何规则
///（整圈均分 / 子弧跨度）一个字都没改，只是换了画哪一层，所以这里要补的
/// 恰好就是"换层时那两层怎么错位"。
///
/// 用户选定的档是 **② 扇面展开 + 保持朝向**（preview/wheel2-preview.html）：
///  - 按角度比例展开成整圈（子弧的 175° → 360°），槽位随之变宽；
///  - 半径带从子环 218–350 收进环一 56–200（这是任何变换都表达不了的等比变形）；
///  - **整圈不旋转**：父项留在它原来的朝向，子弧就从那个方向展开。
///
/// 与悬停鼓出、子弧展开走同一条路：逐帧重算几何。理由是 WPF 的几何不是可动画属性，
/// 而内缘与外缘的缩放系数不同（0.26 与 0.57），MatrixTransform 表达不了。
/// </summary>
public sealed partial class RadialRingView
{
    /// <summary>旧环 2 各项的中心角与跨度 —— 位移的起点。</summary>
    private double[] _shiftFromAngles = [];
    private double[] _shiftFromSweeps = [];

    /// <summary>位移起点的半径带（= 子环那一带）。</summary>
    private double _shiftFromInner;
    private double _shiftFromOuter;

    private LevelShiftAnimator? _levelShift;

    /// <summary>正在做层级位移（供探针与性能核对判断"现在是不是逐帧状态"）。</summary>
    public bool IsLevelShifting => _levelShift?.IsAnimating ?? false;

    /// <summary>
    /// 开始一次层级位移：环 1 的每个扇区从"旧环 2 里它所在的位置"展开到自己的槽位。
    ///
    /// <paramref name="fromSpans"/> 是**重建之前**那份子弧的各项跨度（按同一顺序）。
    /// 项数对不上就不演（宁可硬切）：那说明这次不是"同一层搬家"，比如项数变了。
    /// </summary>
    public void PlayLevelShiftIn(IReadOnlyList<SectorSpan> fromSpans)
    {
        if (_sectors.Length == 0 || fromSpans.Count != _sectors.Length) return;

        var tokens = TokenStore.Current;
        var radius = tokens.Radius;

        _shiftFromAngles = [.. fromSpans.Select(s => s.CenterDeg)];
        _shiftFromSweeps = [.. fromSpans.Select(s => s.SweepDeg)];
        _shiftFromInner = radius.Ring2.Inner;
        _shiftFromOuter = radius.Ring2.Outer;

        _levelShift ??= new LevelShiftAnimator(
            tokens.Motion.LevelShiftMs, tokens.Motion.LevelShiftEasing, WriteLevelShiftGeometry);

        // 先同步落到起点再启动：否则新树会先按终点形态渲染一帧，看起来是"先跳到位再缩回去"。
        WriteLevelShiftGeometry(0);
        _levelShift.Start();
    }

    /// <summary>
    /// 停掉位移并落回基准几何。丢树、隐藏、重置交互时都要调用 ——
    /// 回调挂在 <c>CompositionTarget.Rendering</c>（静态事件）上，留着会让旧树既不被回收又每帧在跑。
    /// </summary>
    public void ResetLevelShift()
    {
        _levelShift?.Reset();
        RestoreBaseGeometry();
    }

    /// <summary>把每个扇区停在"旧环 2 位置"与"环 1 槽位"之间的 <paramref name="progress"/> 处。</summary>
    private void WriteLevelShiftGeometry(double progress)
    {
        var tokens = TokenStore.Current;
        var radius = tokens.Radius;
        var band = radius.Ring2;
        var metrics = RingForms.Metrics(_form, radius.DeadZone, radius.Ring1Outer, radius.SectorGapDegrees);
        var count = _sectors.Length;

        for (var i = 0; i < count; i++)
        {
            // 环 1 里第 i 项的目标槽位：均匀分布的 _spans[i]。
            // 起点是它在旧环 2 里的槽位 —— 两者都是 SectorSpan，插值就是各自插角度与跨度。
            var targetSpan = _spans[i];
            var sweep = Lerp(_shiftFromSweeps[i], targetSpan.SweepDeg, progress);
            var center = Lerp(_shiftFromAngles[i], targetSpan.CenterDeg, progress);
            var inner = Lerp(_shiftFromInner, metrics.Inner, progress);
            var outer = Lerp(_shiftFromOuter, metrics.Outer, progress);

            var geometry = SectorGeometry.ForForm(
                _form, _center, inner, outer,
                center - sweep / 2 + metrics.GapDegrees,
                center + sweep / 2 - metrics.GapDegrees,
                metrics.CornerRadius);

            _sectors[i].Data = geometry;
            if (_halos is not null) _halos[i].Data = geometry;

            // 图标与标签跟着走：它们的位置本来是"按最终半径算一次"，位移期间逐步搬到新半径。
            var iconRadius = inner + (outer - inner) * radius.IconRadiusRatio;
            var labelRadius = inner + (outer - inner) * radius.LabelRadiusRatio;

            if (i < _icons.Length && _icons[i] is not null)
            {
                var offset = Vec2.FromPolar(iconRadius, center);
                var size = _icons[i].Width;
                Canvas.SetLeft(_icons[i], _center.X + offset.X - size / 2);
                Canvas.SetTop(_icons[i], _center.Y + offset.Y - size / 2);
            }

            if (i < _labels.Length && _labels[i] is not null)
                _labels[i].MoveTo(_center, labelRadius, center);
        }

        // 占位图（没图标时画的圆圈 + 首字）不是按索引登记的，但重建时收在 _ring1Placeholders 里，
        // 漏掉它们的话，没有图标的项在原地不动、只有边框在变形。
        for (var i = 0; i < _ring1Placeholders.Count; i++)
        {
            var element = _ring1Placeholders[i];
            var index = i / 2;                                 // 每项两个元素：圆圈 + 首字
            if (index >= count) break;

            var span = _spans[index];
            var sweep = Lerp(_shiftFromSweeps[index], span.SweepDeg, progress);
            var angle = Lerp(_shiftFromAngles[index], span.CenterDeg, progress);
            var inner = Lerp(_shiftFromInner, radius.DeadZone, progress);
            var outer = Lerp(_shiftFromOuter, radius.Ring1Outer, progress);
            var radiusNow = inner + (outer - inner) * radius.IconRadiusRatio;
            var offset = Vec2.FromPolar(radiusNow, angle);
            var size = (element as FrameworkElement)?.Width ?? radius.IconSize;

            Canvas.SetLeft(element, _center.X + offset.X - size / 2);
            Canvas.SetTop(element, _center.Y + offset.Y - size / 2);
            _ = sweep;                                          // 占位图只有位置，没有跨度
        }

        // 到位后交还给基准几何：静止态必须与"从没播过动画"逐像素一致（--perf-probe 锁着这条）。
        if (progress >= 1.0) RestoreBaseGeometry();
    }

    private void RestoreBaseGeometry()
    {
        for (var i = 0; i < _sectors.Length; i++)
        {
            _sectors[i].Data = _baseGeometry[i];
            if (_halos is not null && i < _halos.Length) _halos[i].Data = _baseGeometry[i];
        }
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
