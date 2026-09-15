using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinQuick.App.Design;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using ShapePath = System.Windows.Shapes.Path;

namespace WinQuick.App.Radial;

/// <summary>
/// 拖拽重排的预览。
///
/// 编辑态下按住某项拖动时，被拖的扇区跟着指针走、目标格子用虚线框标出，
/// 松手才真正改数据（<see cref="MenuEditor"/>
/// 那套"按格子重排"：顺序就是数组顺序，角度始终均匀）。
///
/// 几点刻意的取舍：
///
/// 1. **只在角向移动，半径不变。** 径向挪会破坏"这是同一个环"的观感，
///    也会让标签撞上图标 —— 拖拽改的是排列，不是位置。
/// 2. **不重排其它扇区。** 安卓那种"其它图标实时让位"要每帧重画全部扇区、
///    并移动全部图标与文字（≤12 项）。第一版先用"被拖项跟随 + 目标格子虚线"，
///    够看清落点，代价只有重画一个扇区。
/// 3. **被拖项用鼓出的终态"抬起来"**，复用已有的形变量，不再单独做一套高亮。
/// </summary>
public sealed partial class RadialRingView
{
    private int _dragIndex = -1;
    private int _dragTarget = -1;
    private double _dragAngleDeg;
    private ShapePath? _dragMarker;

    /// <summary>正在被拖的项，-1 表示没有。</summary>
    public int DragIndex => _dragIndex;

    /// <summary>是否正在拖拽。</summary>
    public bool IsDragging => _dragIndex >= 0;

    /// <summary>当前指针角对应的目标格子 —— 松手会插到这个位置。</summary>
    public int DragTargetSlot => _dragTarget;

    /// <summary>被拖项当前所在的角度（诊断用）。</summary>
    public double DragAngleDeg => _dragAngleDeg;

    /// <summary>目标格子标记是否真的画出来了 —— 只看"我的状态变量"不算数。</summary>
    public bool DragMarkerAttached => _dragMarker is not null && Root.Children.Contains(_dragMarker);

    /// <summary>开始拖动某一项。</summary>
    public void BeginDrag(int index)
    {
        if (index < 0 || index >= _sectors.Length) return;

        EndDrag();

        _dragIndex = index;
        _dragAngleDeg = _spans[index].CenterDeg;
        _dragTarget = index;

        // 用形变的终态把它"抬起来"，并且**立即落定**（不播动画）：
        // 逐帧动画器每帧都会按 spans[index] 重写几何，那会和下面的预览互相覆盖。
        _bulge.SetHoverImmediate(index);

        UpdateDrag(_dragAngleDeg);
    }

    /// <summary>指针移动时刷新预览。角度是相对圆心的极角。</summary>
    public void UpdateDrag(double angleDeg)
    {
        if (_dragIndex < 0) return;

        _dragAngleDeg = angleDeg;

        var radius = TokenStore.Current.Radius;
        var count = _sectors.Length;
        var sweep = 360.0 / count;

        // 被拖项：保持自己的跨度，中心落在指针角度上；外缘带上鼓出量，看起来是"抬起来"的。
        var half = sweep / 2.0 - radius.SectorGapDegrees;
        _sectors[_dragIndex].Data = SectorGeometry.RingSector(
            _center, radius.DeadZone, radius.Ring1Outer + TokenStore.Current.Motion.HoverBulgePx,
            angleDeg - half, angleDeg + half);

        MoveIconAndLabel(_dragIndex, angleDeg, radius.IconRadius, radius.LabelRadius);

        // 目标格子：离指针角度最近的那一格（格子 0 的中心在 0°）。
        var normalized = SectorSpan.Normalize(angleDeg);
        _dragTarget = (int)Math.Round(normalized / sweep, MidpointRounding.AwayFromZero) % count;

        ShowDragMarker(_dragTarget, sweep);
    }

    /// <summary>结束拖动：清掉目标标记，并把被拖项放回它自己的格子。</summary>
    public void EndDrag()
    {
        if (_dragMarker is not null)
        {
            Root.Children.Remove(_dragMarker);
            _dragMarker = null;
        }

        if (_dragIndex >= 0)
        {
            // 放回原位。若这次拖拽真的改了顺序，紧接着的菜单重建会重新布局，
            // 所以这里只需要保证"没改成"的情况下画面回到原样。
            var radius = TokenStore.Current.Radius;
            var span = _spans[_dragIndex];
            var half = span.SweepDeg / 2.0 - radius.SectorGapDegrees;

            _sectors[_dragIndex].Data = SectorGeometry.RingSector(
                _center, radius.DeadZone, radius.Ring1Outer,
                span.StartDeg + radius.SectorGapDegrees, span.EndDeg - radius.SectorGapDegrees);

            MoveIconAndLabel(_dragIndex, span.CenterDeg, radius.IconRadius, radius.LabelRadius);
            _bulge.SetHoverImmediate(_dragIndex);   // 恢复形变终态，避免留在"抬起"状态
        }

        _dragIndex = -1;
        _dragTarget = -1;
    }

    private void MoveIconAndLabel(int index, double angleDeg, double iconRadius, double labelRadius)
    {
        var icon = _icons[index];
        var iconOffset = Vec2.FromPolar(iconRadius, angleDeg);
        Canvas.SetLeft(icon, _center.X + iconOffset.X - icon.Width / 2);
        Canvas.SetTop(icon, _center.Y + iconOffset.Y - icon.Height / 2);

        // 标签要按角度重新摆 —— 它平时是构造时定位一次就不动的。
        _labels[index].MoveTo(_center, labelRadius, angleDeg);
    }

    private void ShowDragMarker(int slot, double sweep)
    {
        var radius = TokenStore.Current.Radius;

        if (_dragMarker is null)
        {
            _dragMarker = new ShapePath
            {
                Fill = null,
                Stroke = TokenBrushes.Brush(_tokens.Color.HoverStroke, 0.55),
                StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 2.5, 2 },
                IsHitTestVisible = false,
            };
            Root.Children.Add(_dragMarker);
        }

        // 目标就是它自己时不画 —— 画了会让人以为"松手一定会换位置"。
        if (slot == _dragIndex)
        {
            _dragMarker.Visibility = Visibility.Collapsed;
            return;
        }

        _dragMarker.Visibility = Visibility.Visible;
        _dragMarker.Data = SectorGeometry.RingSector(
            _center, radius.DeadZone + 6, radius.Ring1Outer - 2,
            slot * sweep - sweep / 2.0 + radius.SectorGapDegrees,
            slot * sweep + sweep / 2.0 - radius.SectorGapDegrees);
    }

    /// <summary>把命中点换算成"离圆心多远、什么角度"，供拖拽用。</summary>
    public (double Distance, double AngleDeg) PolarOf(Point localPoint)
    {
        var offset = new Vec2(localPoint.X - _center.X, localPoint.Y - _center.Y);
        return (offset.Length, offset.AngleDeg);
    }
}
