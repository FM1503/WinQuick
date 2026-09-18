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

    /// <summary>
    /// 按形态生成扇区路径。四种形态共用同一套角度区间（命中测试因此不用改），
    /// 差别只在单元外形 —— 花瓣内侧收尖、圆角把尖角磨圆、刻度盘把外缘收进去给刻度让位。
    /// </summary>
    public static PathGeometry ForForm(
        RingForm form, Point center, double innerRadius, double outerRadius,
        double startDeg, double endDeg, double cornerRadius) => form switch
    {
        RingForm.Rounded => RoundedRingSector(center, innerRadius, outerRadius, startDeg, endDeg, cornerRadius),
        RingForm.Petal => PetalSector(center, innerRadius, outerRadius, startDeg, endDeg),
        _ => RingSector(center, innerRadius, outerRadius, startDeg, endDeg),
    };

    /// <summary>
    /// 圆角环形扇区：把两个尖角换成**以尖角为控制点的二次曲线**。
    ///
    /// 这不是精确的内切圆弧，但视觉上就是圆角，而且只用到已有的段类型
    /// （ArcSegment + QuadraticBezierSegment），不必去解内切圆的几何。
    /// </summary>
    public static PathGeometry RoundedRingSector(
        Point center, double innerRadius, double outerRadius,
        double startDeg, double endDeg, double cornerRadius)
    {
        var sweep = endDeg - startDeg;
        var corner = Math.Max(0.5, Math.Min(cornerRadius, sweep * 0.2));

        // 角在角度上的占位：弧长 = r·θ，所以 θ = corner / r（弧度）。
        // 上限取 1/4 跨度，防止窄扇区上两个角撞在一起导致图形退化。
        var maxDeg = sweep / 4;
        var daIn = Math.Min(corner / innerRadius * 180.0 / Math.PI, maxDeg);
        var daOut = Math.Min(corner / outerRadius * 180.0 / Math.PI, maxDeg);
        var isLarge = sweep > 180;

        var figure = new PathFigure
        {
            StartPoint = Polar(center, innerRadius, startDeg + daIn),
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new ArcSegment(
            Polar(center, innerRadius, endDeg - daIn), new Size(innerRadius, innerRadius), 0, false,
            SweepDirection.Clockwise, true));
        figure.Segments.Add(new QuadraticBezierSegment(
            Polar(center, innerRadius, endDeg), Polar(center, outerRadius, endDeg - daOut), true));
        figure.Segments.Add(new ArcSegment(
            Polar(center, outerRadius, startDeg + daOut), new Size(outerRadius, outerRadius), 0, isLarge,
            SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new QuadraticBezierSegment(
            Polar(center, outerRadius, startDeg), Polar(center, innerRadius, startDeg + daIn), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 花瓣：内侧收成一个尖、外侧是宽圆弧（叶片形）。
    ///
    /// 外侧用真实圆弧（半径 = 外径），两侧用二次曲线从外缘回到内侧的尖点，
    /// 控制点取中段半径处、角向略外扩 —— 侧面因此微微外凸，像一片叶子而不是三角形。
    /// </summary>
    public static PathGeometry PetalSector(
        Point center, double innerRadius, double outerRadius, double startDeg, double endDeg)
    {
        var mid = (startDeg + endDeg) / 2;
        var half = (endDeg - startDeg) / 2;
        var waist = innerRadius + ((outerRadius - innerRadius) * 0.5);
        var bow = half * 1.18;
        var isLarge = (endDeg - startDeg) > 180;

        var figure = new PathFigure
        {
            StartPoint = Polar(center, innerRadius, mid),
            IsClosed = true,
            IsFilled = true,
        };
        figure.Segments.Add(new QuadraticBezierSegment(
            Polar(center, waist, mid - bow), Polar(center, outerRadius, mid - half), true));
        figure.Segments.Add(new ArcSegment(
            Polar(center, outerRadius, mid + half), new Size(outerRadius, outerRadius), 0, isLarge,
            SweepDirection.Clockwise, true));
        figure.Segments.Add(new QuadraticBezierSegment(
            Polar(center, waist, mid + bow), Polar(center, innerRadius, mid), true));

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
