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
        var isLarge = endDeg - startDeg > 180;

        var outerStart = Polar(center, outerRadius, startDeg);
        var outerEnd = Polar(center, outerRadius, endDeg);
        var innerEnd = Polar(center, innerRadius, endDeg);
        var innerStart = Polar(center, innerRadius, startDeg);

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(
            outerEnd, new Size(outerRadius, outerRadius), 0, isLarge, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(innerEnd, true));
        figure.Segments.Add(new ArcSegment(
            innerStart, new Size(innerRadius, innerRadius), 0, isLarge, SweepDirection.Counterclockwise, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>极坐标转屏幕坐标（y 轴向下）。</summary>
    public static Point Polar(Point center, double radius, double deg)
    {
        var offset = Vec2.FromPolar(radius, deg);
        return new Point(center.X + offset.X, center.Y + offset.Y);
    }
}
