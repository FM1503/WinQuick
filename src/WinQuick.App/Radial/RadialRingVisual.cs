using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinQuick.App.Design;
using WinQuick.App.Interop;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using WinQuick.Core.Motion;
using ShapePath = System.Windows.Shapes.Path;

namespace WinQuick.App.Radial;

/// <summary>
/// 轮盘的视觉构造。
///
/// 同一份代码被两处使用：运行时窗口（<see cref="RadialWindow"/>）与离屏预览
/// （`--render-preview`）。共用是刻意的 —— 否则「预览好看、实际运行不一样」这类分叉
/// 会一直存在，而 PNG 数值验证就失去意义了。
///
/// 所有尺寸与配色都来自 <see cref="DesignTokens"/>（design/tokens.json），
/// 代码里不写死任何颜色或半径。几何上限受内存预算约束，原因写在 tokens.json 里。
///
/// 刻意不用 WPF 的 Effect（DropShadowEffect / BlurEffect）：每个 Effect 都要占一张
/// 中间表面，叠多了整个窗口会退化到 WARP 软件渲染，那是 WPF 界面掉帧最常见的原因。
/// 层次感用描边与明暗对比表达。
/// </summary>
public sealed class RadialRingView
{
    private readonly DesignTokens _tokens;
    private readonly Point _center;
    private readonly SectorSpan[] _spans;
    private readonly ShapePath[] _sectors;
    private readonly SectorLabel[] _labels;
    private readonly Image[] _icons;
    private readonly ShapePath?[] _childMarkers;
    private readonly SectorBulgeAnimator _bulge;
    private int _hover = -1;

    internal RadialRingView(
        FrameworkElement root, DesignTokens tokens, Point center, SectorSpan[] spans,
        ShapePath[] sectors, SectorLabel[] labels, Image[] icons, ShapePath?[] childMarkers,
        SectorBulgeAnimator bulge)
    {
        Root = root;
        _tokens = tokens;
        _center = center;
        _spans = spans;
        _sectors = sectors;
        _labels = labels;
        _icons = icons;
        _childMarkers = childMarkers;
        _bulge = bulge;
    }

    public FrameworkElement Root { get; }

    public int SectorCount => _sectors.Length;
    public int HoverIndex => _hover;

    /// <summary>供离屏预览与探针核对「图标是否真的画出来了」，不信任视觉判断。</summary>
    public int RenderedIconCount => _icons.Count(i => i.Source is not null);

    public int ChildMarkerCount => _childMarkers.Count(m => m is not null);

    /// <summary>需要滚动才能看全的标签数 —— 供验证「长文本是否被妥善处理」。</summary>
    public int ScrollNeededCount => _labels.Count(l => l.NeedsScroll);

    /// <summary>被自动缩小过字号的标签数。</summary>
    public int ShrunkLabelCount => _labels.Count(l => l.FontSize < _tokens.Typography.LabelFontSize);

    /// <summary>各标签的最大溢出像素数，0 表示全部放得下。</summary>
    public double MaxLabelOverflow => _labels.Length == 0 ? 0 : _labels.Max(l => l.OverflowPixels);

    /// <summary>发生折行的标签数。</summary>
    public int WrappedLabelCount => _labels.Count(l => l.IsWrapped);

    /// <summary>被悬停扇区当前向外鼓出的像素数 —— 供验证形变确实发生了。</summary>
    public double CurrentHoverBulge => _bulge.CurrentBulgeOf(_hover);

    /// <summary>形变动画是否还在进行中。</summary>
    public bool IsBulgeAnimating => _bulge.IsAnimating;

    public void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;

        var colors = _tokens.Color;
        var normalFill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        var normalStroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);
        var hoverFill = TokenBrushes.Brush(colors.HoverFill);
        var hoverStroke = TokenBrushes.Brush(colors.HoverStroke, colors.HoverStrokeAlpha);

        for (var i = 0; i < _sectors.Length; i++)
        {
            var hovered = i == index;
            _sectors[i].Fill = hovered ? hoverFill : normalFill;
            _sectors[i].Stroke = hovered ? hoverStroke : normalStroke;
            _labels[i].SetHover(hovered);
            // 图标与子菜单标记跟着扇区明暗走，否则高亮时它们会像浮在错误的层上。
            _icons[i].Opacity = hovered ? 1.0 : 0.94;
            if (_childMarkers[i] is { } marker)
                marker.Stroke = hovered ? hoverStroke : normalStroke;
        }

        // 几何形变交给逐帧动画器：颜色高亮是即时反馈，外扩是有形的位移，
        // 两者时长不同，所以分开处理。
        _bulge.SetHover(index);
    }

    /// <summary>把窗口内的坐标交给 Core 的命中测试，返回落在哪个区域/扇区。</summary>
    public HitResult HitTest(Point localPoint) =>
        RadialLayout.HitTest(
            new Vec2(localPoint.X - _center.X, localPoint.Y - _center.Y),
            _tokens.Radius.DeadZone,
            _tokens.Radius.Ring1Outer,
            _spans);

    // ---------------------------------------------------------------- 构造

    public static RadialRingView Build(MenuSpec menu, double? sizeOverride = null)
    {
        var tokens = TokenStore.Current;
        var radius = tokens.Radius;
        var size = sizeOverride ?? radius.WindowSize;
        var center = new Point(size / 2, size / 2);

        var root = new Canvas
        {
            Width = size,
            Height = size,
            // Transparent（而非 null）才可命中测试：整窗范围都能收到鼠标事件，
            // 「点圆盘外关闭」才成立。
            Background = Brushes.Transparent,
        };

        // 所有角度都走 Core 的 RadialLayout：渲染与命中测试共用同一份数学，
        // 不会出现「看着在扇区 A、点下去选中 B」。
        var count = menu.Items.Count;
        var spans = RadialLayout.Distribute(count);

        var sectors = new ShapePath[count];
        var baseGeometry = new PathGeometry[count];
        var labels = new SectorLabel[count];
        var icons = new Image[count];
        var childMarkers = new ShapePath?[count];

        var colors = tokens.Color;
        var fill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        var stroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);

        for (var i = 0; i < count; i++)
        {
            var span = spans[i];
            var item = menu.Items[i];

            var geometry = SectorGeometry.RingSector(
                center, radius.DeadZone, radius.Ring1Outer,
                span.StartDeg + radius.SectorGapDegrees,
                span.EndDeg - radius.SectorGapDegrees);

            var sector = new ShapePath
            {
                Data = geometry,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            sectors[i] = sector;
            baseGeometry[i] = geometry;
            root.Children.Add(sector);

            icons[i] = AddIcon(root, center, radius.IconRadius, span.CenterDeg, item, radius.IconSize);
            labels[i] = new SectorLabel(
                root, center, radius.LabelRadius, span.CenterDeg, item.Label,
                tokens.Typography.LabelFontSize,
                maxWidth: RadiusTokens.LabelMaxHalfWidth(radius, count) * 2,
                maxHeight: tokens.Typography.LabelFontSize * radius.LabelLineHeightFactor * radius.LabelMaxLines,
                lineHeightFactor: radius.LabelLineHeightFactor,
                foreground: TokenBrushes.Brush(colors.Label));

            if (item.HasChildren)
                childMarkers[i] = AddChildMarker(root, center, radius.Ring1Outer, span.CenterDeg, stroke);
        }

        // 中心死区：语义是「取消」。用一块内凹的圆暗示它不可点选。
        AddEllipse(root, center, radius.DeadZone - 4,
            TokenBrushes.Brush(colors.DeadZoneFill),
            TokenBrushes.Brush(colors.DeadZoneStroke, colors.DeadZoneStrokeAlpha), 1);
        AddText(root, center, 0, 0, "Esc",
            TokenBrushes.Brush(colors.Hint, colors.HintAlpha), tokens.Typography.HintFontSize);

        var bulge = new SectorBulgeAnimator(
            sectors, baseGeometry, center,
            radius.DeadZone, radius.Ring1Outer, spans, radius.SectorGapDegrees,
            tokens.Motion.HoverBulgePx, tokens.Motion.EntranceEasing, tokens.Motion.HoverBulgeMs);

        return new RadialRingView(
            root, tokens, center, spans, sectors, labels, icons, childMarkers, bulge);
    }

    /// <summary>
    /// 立即把悬停状态落定（含形变终态），不播放动画。仅供离屏渲染与验证使用。
    /// </summary>
    public void SetHoverImmediate(int index)
    {
        SetHover(index);
        _bulge.SetHoverImmediate(index);
    }

    /// <summary>
    /// 把形变复位。覆盖层隐藏时必须调用 —— 否则残留的逐帧回调会在窗口不可见时继续跑。
    /// </summary>
    public void ResetInteraction()
    {
        _bulge.Reset();
        _hover = -1;
    }

    /// <summary>
    /// 图标。取不到 shell 图标时画首字占位 —— 一个缺失的图标不该让整项看起来像坏了。
    /// </summary>
    private static Image AddIcon(
        Canvas root, Point center, double radius, double angleDeg, MenuItemSpec item, double iconSize)
    {
        var image = new Image
        {
            Width = iconSize,
            Height = iconSize,
        };
        // 图标源是 96px，最终要缩放到 34 DIP × 屏幕缩放（本机约 60px），
        // 必须用高质量插值，否则边缘发毛。
        // RenderOptions 是附加属性，要用静态 setter，不能用 XAML 那种对象初始值写法。
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var source = ShellIcons.Get(item.IconPath);
        if (source is not null) image.Source = source;
        else AddPlaceholder(root, center, radius, angleDeg, item.Label, iconSize);

        var offset = Vec2.FromPolar(radius, angleDeg);
        Canvas.SetLeft(image, center.X + offset.X - iconSize / 2);
        Canvas.SetTop(image, center.Y + offset.Y - iconSize / 2);
        root.Children.Add(image);
        return image;
    }

    private static void AddPlaceholder(
        Canvas root, Point center, double radius, double angleDeg, string label, double iconSize)
    {
        var tokens = TokenStore.Current;
        var half = iconSize / 2;

        var circle = new Ellipse
        {
            Width = iconSize,
            Height = iconSize,
            Fill = TokenBrushes.Brush(tokens.Color.BadgeFill, tokens.Color.BadgeFillAlpha),
            Stroke = TokenBrushes.Brush(tokens.Color.SectorStroke, tokens.Color.SectorStrokeAlpha),
            StrokeThickness = 1,
        };

        var offset = Vec2.FromPolar(radius, angleDeg);
        Canvas.SetLeft(circle, center.X + offset.X - half);
        Canvas.SetTop(circle, center.Y + offset.Y - half);
        root.Children.Add(circle);

        var initial = string.IsNullOrEmpty(label) ? "?" : label[..1];
        AddText(root, center, radius, angleDeg, initial,
            TokenBrushes.Brush(tokens.Color.Label, 0.9), iconSize * 0.5);
    }

    /// <summary>子菜单指示：贴着扇区外缘画一个折角，指向圆心之外。</summary>
    private static ShapePath AddChildMarker(
        Canvas root, Point center, double outerRadius, double angleDeg, Brush stroke)
    {
        const double middleInset = 14;
        const double halfWidthDeg = 5.5;

        var middleRadius = outerRadius - middleInset;
        var left = Vec2.FromPolar(middleRadius, angleDeg - halfWidthDeg);
        var tip = Vec2.FromPolar(outerRadius - 8, angleDeg);
        var right = Vec2.FromPolar(middleRadius, angleDeg + halfWidthDeg);

        var figure = new PathFigure
        {
            StartPoint = new Point(center.X + left.X, center.Y + left.Y),
            IsClosed = false,
        };
        figure.Segments.Add(new LineSegment(new Point(center.X + tip.X, center.Y + tip.Y), true));
        figure.Segments.Add(new LineSegment(new Point(center.X + right.X, center.Y + right.Y), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        var marker = new ShapePath
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
        root.Children.Add(marker);
        return marker;
    }

    private static void AddEllipse(
        Canvas root, Point center, double radius, Brush? fill, Brush? stroke, double strokeThickness)
    {
        var ellipse = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = strokeThickness,
        };
        Canvas.SetLeft(ellipse, center.X - radius);
        Canvas.SetTop(ellipse, center.Y - radius);
        root.Children.Add(ellipse);
    }

    private static TextBlock AddText(
        Canvas root, Point center, double radius, double angleDeg, string text,
        Brush brush, double fontSize, string? fontFamily = null)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = fontSize,
            FontFamily = new FontFamily(fontFamily ?? TokenStore.Current.Typography.LabelFontFamily),
            TextAlignment = TextAlignment.Center,
        };

        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var offset = Vec2.FromPolar(radius, angleDeg);
        Canvas.SetLeft(label, center.X + offset.X - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, center.Y + offset.Y - label.DesiredSize.Height / 2);
        root.Children.Add(label);
        return label;
    }

}
