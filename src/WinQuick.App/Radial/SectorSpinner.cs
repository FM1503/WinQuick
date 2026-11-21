using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinQuick.App.Design;

namespace WinQuick.App.Radial;

/// <summary>
/// 子弧图标位的环形加载占位（spec §2.3，用户从 preview/spinner-preview.html 终选的②档：
/// 粗弧变弧长，Material 3 circular indeterminate 的观感）。
///
/// 两层 Ellipse：底环 15% 透明度 + 前景 3px 弧（DashArray 切出 ~66% 弧段）；
/// 动画只有 RotateTransform 与 StrokeDashOffset 两个依赖属性 —— 无 Effect、无位图渲染，
/// 分层窗口的每帧合成预算不受影响（见 docs/M0-findings.md / M6 的代价结论）。
/// 图标就绪或抽取失败时由调用方直接移除本元素。
/// </summary>
public static class SectorSpinner
{
    public static FrameworkElement Create(double iconSize, Brush foreground)
    {
        const double stroke = 3.0;
        var d = iconSize - stroke;                 // 直径（留线宽）
        var circumferenceUnits = Math.PI * d / stroke; // DashArray 单位是线宽的倍数

        var grid = new Grid { Width = iconSize, Height = iconSize };

        var track = new Ellipse
        {
            Width = d, Height = d,
            StrokeThickness = stroke,
            Stroke = CloneWithOpacity(foreground, 0.15),
        };
        var arc = new Ellipse
        {
            Width = d, Height = d,
            StrokeThickness = stroke,
            Stroke = foreground,
            StrokeDashArray = new DoubleCollection { circumferenceUnits * 0.66, circumferenceUnits * 0.34 },
        };
        var rotate = new RotateTransform();
        arc.RenderTransform = rotate;
        arc.RenderTransformOrigin = new Point(0.5, 0.5);

        grid.Children.Add(track);
        grid.Children.Add(arc);

        var spin = new DoubleAnimation
        {
            From = 0, To = 360, Duration = TimeSpan.FromMilliseconds(1200),
            RepeatBehavior = RepeatBehavior.Forever,
            // Material 3 的「标准 easing」近似：二次缓入缓出（每圈 4 个变速点，观感接近原型页）。
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        // 弧段沿圆周缓慢滑移，近似 Material 的「弧长呼吸」观感（DashOffset 动画比 DashArray 动画便宜得多）。
        var slide = new DoubleAnimation
        {
            From = 0, To = circumferenceUnits,
            Duration = TimeSpan.FromMilliseconds(2400),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, arc);
        Storyboard.SetTargetProperty(spin, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        Storyboard.SetTarget(slide, arc);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(Shape.StrokeDashOffset)"));

        var board = new Storyboard { Duration = TimeSpan.FromMilliseconds(2400) };
        board.Children.Add(spin);
        board.Children.Add(slide);
        board.RepeatBehavior = RepeatBehavior.Forever;
        // 探针识别标（ChildArcPerfProbe 数 spinner 残留用）；Tag 不参与任何逻辑。
        grid.Tag = "sector-spinner";
        grid.Loaded += (_, _) => board.Begin(arc, true);
        grid.Unloaded += (_, _) => board.Stop(arc);
        return grid;
    }

    /// <summary>克隆画刷并压透明度 —— 不改共享画刷实例（它可能在多处使用）。</summary>
    private static Brush CloneWithOpacity(Brush source, double opacity)
    {
        if (source is SolidColorBrush sc)
            return new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(sc.Color.A * opacity), sc.Color.R, sc.Color.G, sc.Color.B));
        var clone = source.Clone();
        clone.Opacity = opacity;
        return clone;
    }
}
