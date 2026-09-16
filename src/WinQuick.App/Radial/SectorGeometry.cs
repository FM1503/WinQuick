using System.Windows;
using System.Windows.Media;
using WinQuick.Core.Geometry;

namespace WinQuick.App.Radial;

/// <summary>
/// 扇区的 WPF 几何构造。
///
/// 独立成类而不是挂在视图上：它被两处使用 —— 视图（初次构造）与悬停形变动画器
/// （逐帧重建外扩后的扇区）。挂在"视图"类上会让动画器去调用一个语义上不属于它的成员。
///
/// 角度约定与 Core 的 RadialLayout 一致：0° = 正上，顺时针。
/// </summary>
public static class SectorGeometry
{
    /// <summary>环形扇区路径。结果已冻结 —— 它会被多个图形共享。</summary>
    public static PathGeometry RingSector(
        Point center, double innerRadius, double outerRadius, double startDeg, double endDeg)
    {
        var geometry = new PathGeometry();
        RingSectorInto(geometry, center, innerRadius, outerRadius, startDeg, endDeg);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 把环形扇区写进一个**可变的**几何里（就地改点，不重新分配、不冻结）。
    ///
    /// 为什么需要它：逐帧驱动几何的动画（逐格入场）每帧都要改一次外缘，
    /// 而 <see cref="RingSector"/> 每次都新建 5 个对象再冻结。
    /// 实测：同样 460ms 的入场，逐帧重建几何比整窗缩放**多出一倍的长帧**
    ///（13~15 帧 vs 7~8 帧，超过 20ms）—— 所以这条路径值得就地改。
    ///
    /// 用法上有一个约束：这个几何**不能冻结**，也不能被多个图形共享。
    /// 需要共享的静止几何仍然走 <see cref="RingSector"/>。
    /// </summary>
    public static void RingSectorInto(
        PathGeometry geometry, Point center, double innerRadius, double outerRadius,
        double startDeg, double endDeg)
    {
        var isLarge = endDeg - startDeg > 180;

        var outerStart = Polar(center, outerRadius, startDeg);
        var outerEnd = Polar(center, outerRadius, endDeg);
        var innerEnd = Polar(center, innerRadius, endDeg);
        var innerStart = Polar(center, innerRadius, startDeg);

        if (geometry.Figures.Count == 0)
        {
            var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
            figure.Segments.Add(new ArcSegment(
                outerEnd, new Size(outerRadius, outerRadius), 0, isLarge, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(innerEnd, true));
            figure.Segments.Add(new ArcSegment(
                innerStart, new Size(innerRadius, innerRadius), 0, isLarge,
                SweepDirection.Counterclockwise, true));
            geometry.Figures.Add(figure);
            return;
        }

        // 第二次以后：同一个几何对象上改点。WPF 会照常发一次 Changed 通知并重绘，
        // 但不必重新分配对象图，也不必走冻结/解冻那套。
        var existing = geometry.Figures[0];
        existing.StartPoint = outerStart;

        var outerArc = (ArcSegment)existing.Segments[0];
        outerArc.Point = outerEnd;
        outerArc.Size = new Size(outerRadius, outerRadius);
        outerArc.IsLargeArc = isLarge;

        ((LineSegment)existing.Segments[1]).Point = innerEnd;

        var innerArc = (ArcSegment)existing.Segments[2];
        innerArc.Point = innerStart;
        innerArc.Size = new Size(innerRadius, innerRadius);
        innerArc.IsLargeArc = isLarge;
    }

    /// <summary>极坐标转屏幕坐标（y 轴向下）。</summary>
    public static Point Polar(Point center, double radius, double deg)
    {
        var offset = Vec2.FromPolar(radius, deg);
        return new Point(center.X + offset.X, center.Y + offset.Y);
    }
}
