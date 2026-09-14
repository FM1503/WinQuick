using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinQuick.App.Design;
using WinQuick.App.Interop;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using ShapePath = System.Windows.Shapes.Path;

namespace WinQuick.App.Radial;

/// <summary>
/// 子弧（第二层环）的渲染与命中测试。
///
/// 形态：同心多层环 —— 子弧画在父扇区**外侧**的环带上，以父扇区的中心角为中线。
/// 这样父级仍然可见，且子项与父项方向一致。方向一致是第二层能形成肌肉记忆的前提：
/// CHI'93 的实测结论是时间随层级线性增长、真正的限制因素是错误率，
/// 而「同一方向继续」正是把错误率压下来的关键。
///
/// 子弧的角度跨度默认 90°，但会被 childMinSectorDegrees × 子项数顶大 ——
/// 12 扇区父级下 90° 里塞 8 个子项只剩 11°，那种宽度点不中。
/// </summary>
public sealed partial class RadialRingView
{
    private readonly List<ShapePath> _childSectors = [];
    private readonly List<Image> _childIcons = [];
    private readonly List<SectorLabel> _childLabels = [];
    private readonly List<ShapePath?> _childChildMarkers = [];

    /// <summary>
    /// 子弧里除扇区/图标/标记之外的图形（目前是取不到图标时画的占位图）。
    /// 必须单独登记 —— 它们由 AddPlaceholder 直接加进画布，不在上面几个列表里，
    /// 漏登记的话收起后不会移除，每展开一次就残留一份。
    /// </summary>
    private readonly List<UIElement> _childExtraVisuals = [];

    private SectorSpan[] _childSpans = [];
    private SectorSpan _childArcSpan;
    private int _childArcParent = -1;
    private int _hoveredChild = -1;
    private double _childArcProgress;
    private ChildArcAnimator? _childAnimator;
    private bool _childArcRemoving;

    /// <summary>
    /// 展开某个父扇区的子弧。已有子弧会立刻被替换（同时只显示一个子弧）。
    /// </summary>
    public void ExpandChildArc(int parentIndex, bool immediate = false)
    {
        if (parentIndex < 0 || parentIndex >= _menu.Items.Count) return;

        var children = _menu.Items[parentIndex].Children;
        if (children.Count == 0) return;

        if (_childArcParent == parentIndex && !_childArcRemoving)
        {
            if (immediate) SetChildArcImmediate(true);
            return;
        }

        RemoveChildVisuals();
        _childArcRemoving = false;
        _childArcParent = parentIndex;
        _hoveredChild = -1;

        var tokens = TokenStore.Current;
        var radius = tokens.Radius;
        var band = radius.Ring2;
        var parentCenter = _spans[parentIndex].CenterDeg;

        _childArcSpan = RadialLayout.ChildArc(
            parentCenter, children.Count, tokens.Interaction.ChildSpanDegrees);
        _childSpans = RadialLayout.DistributeWithin(_childArcSpan, children.Count);

        var colors = tokens.Color;
        var fill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        var stroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);

        var iconRadius = band.At(radius.IconRadiusRatio);
        var labelRadius = band.At(radius.LabelRadiusRatio);

        // 每个子扇区自己的角度跨度差别很大（2 个子项各 45°、8 个子项各 25°），
        // 所以标签宽度必须按各自的跨度算，不能沿用第一层环的。
        var childSweep = _childArcSpan.SweepDeg / children.Count;
        var maxHalfWidth = RadiusTokens.LabelMaxHalfWidth(
            iconRadius, labelRadius, radius.IconSize, childSweep, radius.SectorGapDegrees);

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var span = _childSpans[i];

            var sector = new ShapePath
            {
                Data = SectorGeometry.RingSector(
                    _center, band.Inner, band.Inner + band.Width * _childArcProgress,
                    span.StartDeg + radius.SectorGapDegrees,
                    span.EndDeg - radius.SectorGapDegrees),
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            _childSectors.Add(sector);
            Root.Children.Add(sector);

            _childIcons.Add(AddChildIcon(child, iconRadius, span.CenterDeg, radius.IconSize));
            // 占位图与图标是互斥的：有图标就不画占位。两者都登记，清理时一并移除。
            _childLabels.Add(new SectorLabel(
                Root, _center, labelRadius, span.CenterDeg, child.Label,
                tokens.Typography.LabelFontSize,
                maxWidth: maxHalfWidth * 2,
                maxHeight: tokens.Typography.LabelFontSize * radius.LabelLineHeightFactor * radius.LabelMaxLines,
                lineHeightFactor: radius.LabelLineHeightFactor,
                foreground: TokenBrushes.Brush(colors.Label)));
            _childChildMarkers.Add(child.HasChildren
                ? AddGrandchildMarker(band.Outer, span.CenterDeg, stroke)
                : null);
        }

        // 子弧也要参与悬停形变，否则悬停子项时没有外扩反馈。
        _childAnimator ??= new ChildArcAnimator(
            tokens.Motion.ChildArcDurationMs, tokens.Motion.ChildArcEasing, OnChildArcProgress);

        if (immediate) SetChildArcImmediate(true);
        else _childAnimator.Expand();
    }

    /// <summary>收起子弧（带动画）。</summary>
    public void CollapseChildArc()
    {
        if (_childArcParent < 0 || _childArcRemoving) return;

        _childArcRemoving = true;
        _hoveredChild = -1;
        ApplyChildHover();

        if (_childAnimator is null) { CollapseChildArcImmediate(); return; }
        _childAnimator.Collapse();
    }

    /// <summary>立即收起并移除子弧视觉，不播放动画。</summary>
    public void CollapseChildArcImmediate()
    {
        _childAnimator?.Reset();
        RemoveChildVisuals();
        _childArcParent = -1;
        _childArcRemoving = false;
        _hoveredChild = -1;
        _childArcProgress = 0;
    }

    /// <summary>立即把子弧设为展开或收起态，不播放动画。供离屏渲染使用。</summary>
    public void SetChildArcImmediate(bool expanded)
    {
        if (expanded)
        {
            _childArcProgress = 1;
            _childAnimator?.SetImmediate(1);
            RebuildChildGeometry();
        }
        else
        {
            CollapseChildArcImmediate();
        }
    }

    /// <summary>设置子项的悬停高亮。</summary>
    public void SetChildHover(int index)
    {
        if (index == _hoveredChild) return;
        _hoveredChild = index;
        ApplyChildHover();
    }

    private void ApplyChildHover()
    {
        var tokens = TokenStore.Current;
        var colors = tokens.Color;
        var normalFill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        var normalStroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);
        var hoverFill = TokenBrushes.Brush(colors.HoverFill);
        var hoverStroke = TokenBrushes.Brush(colors.HoverStroke, colors.HoverStrokeAlpha);

        for (var i = 0; i < _childSectors.Count; i++)
        {
            var hovered = i == _hoveredChild;
            _childSectors[i].Fill = hovered ? hoverFill : normalFill;
            _childSectors[i].Stroke = hovered ? hoverStroke : normalStroke;
            _childLabels[i].SetHover(hovered);
            _childIcons[i].Opacity = hovered ? 1.0 : 0.94;
            if (_childChildMarkers[i] is { } marker)
                marker.Stroke = hovered ? hoverStroke : normalStroke;
        }
    }

    private void OnChildArcProgress(double progress)
    {
        _childArcProgress = progress;
        RebuildChildGeometry();

        if (progress > 0.001 || !_childArcRemoving) return;

        // 收起动画走完，必须做**完整**善后：拆掉视觉，并且清掉"子弧存在"这个状态。
        //
        // 只拆视觉不清状态是个隐患：视图会一直认为子弧还开着，于是 Esc 与
        // 点中心会去执行"返回上一层"而不是退出 —— 表现为第一次按没反应、要按两次。
        // 这个 bug 就是被 --overlay-probe 的收起核对抓到的。
        CollapseChildArcImmediate();
    }

    /// <summary>
    /// 按当前进度重建子扇区几何：内外半径从重合（无厚度）逐渐拉开到完整环带。
    /// 这就是「从父扇区方向生长出来」这个观感的来源。
    /// </summary>
    private void RebuildChildGeometry()
    {
        var radius = TokenStore.Current.Radius;
        var band = radius.Ring2;

        var inner = band.Inner;
        var outer = band.Inner + band.Width * _childArcProgress;

        for (var i = 0; i < _childSectors.Count; i++)
        {
            var span = _childSpans[i];
            _childSectors[i].Data = SectorGeometry.RingSector(
                _center, inner, outer,
                span.StartDeg + radius.SectorGapDegrees,
                span.EndDeg - radius.SectorGapDegrees);
        }

        // 图标与文字的透明度跟随进度，否则它们会在扇区还很薄的时候先冒出来。
        var opacity = Math.Clamp(_childArcProgress, 0, 1);
        for (var i = 0; i < _childIcons.Count; i++)
        {
            _childIcons[i].Opacity = opacity * (i == _hoveredChild ? 1.0 : 0.94);
            _childLabels[i].SetOpacity(opacity);
        }
    }

    private void RemoveChildVisuals()
    {
        foreach (var visual in _childSectors.Cast<UIElement>()
                     .Concat(_childIcons)
                     .Concat(_childChildMarkers.Where(m => m is not null).Cast<UIElement>())
                     .Concat(_childExtraVisuals))
        {
            Root.Children.Remove(visual);
        }

        _childExtraVisuals.Clear();

        foreach (var label in _childLabels)
            label.Detach();

        _childSectors.Clear();
        _childIcons.Clear();
        _childLabels.Clear();
        _childChildMarkers.Clear();
        _childSpans = [];
    }

    private Image AddChildIcon(MenuItemSpec child, double iconRadius, double angleDeg, double iconSize)
    {
        var image = new Image { Width = iconSize, Height = iconSize };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Opacity = _childArcProgress;

        var source = ShellIcons.Get(child.IconPath);
        if (source is not null)
        {
            image.Source = source;
        }
        else
        {
            // 与第一层环一致：取不到图标就画首字占位，而不是留一块空白。
            // 把占位图收进 _childExtraVisuals，否则收起时会残留在视觉树上。
            AddPlaceholder(Root, _center, iconRadius, angleDeg, child.Label, iconSize,
                _childExtraVisuals);
        }

        var offset = Vec2.FromPolar(iconRadius, angleDeg);
        Canvas.SetLeft(image, _center.X + offset.X - iconSize / 2);
        Canvas.SetTop(image, _center.Y + offset.Y - iconSize / 2);

        // 必须挂到视觉树上才会被渲染。
        // 这里漏掉过一次：函数返回了 Image 对象，调用方也把它收进了 _childIcons，
        // 但没有任何一行把它加进画布 —— 结果是子弧只有文字、没有图标，
        // 而对象模型里一切看起来都正常。同类错误（对象建好但没接进树）已经出现过两次。
        Root.Children.Add(image);

        return image;
    }

    private ShapePath? AddGrandchildMarker(double bandOuter, double angleDeg, Brush stroke)
    {
        const double middleInset = 14;
        const double halfWidthDeg = 5.5;

        var middleRadius = bandOuter - middleInset;
        var left = Vec2.FromPolar(middleRadius, angleDeg - halfWidthDeg);
        var tip = Vec2.FromPolar(bandOuter - 8, angleDeg);
        var right = Vec2.FromPolar(middleRadius, angleDeg + halfWidthDeg);

        var figure = new PathFigure
        {
            StartPoint = new Point(_center.X + left.X, _center.Y + left.Y),
            IsClosed = false,
        };
        figure.Segments.Add(new LineSegment(new Point(_center.X + tip.X, _center.Y + tip.Y), true));
        figure.Segments.Add(new LineSegment(new Point(_center.X + right.X, _center.Y + right.Y), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        var marker = new ShapePath
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            Opacity = _childArcProgress,
        };
        Root.Children.Add(marker);
        return marker;
    }

    /// <summary>
    /// 命中测试。先看子弧环带，再看第一层环 —— 两者的半径区间不重叠
    /// （环一到 200，子环从 206 起），所以顺序其实不敏感，但先查子弧更贴合"越上层越优先"。
    /// </summary>
    private HitResult HitTestWithChildArc(Vec2 offset)
    {
        var radius = TokenStore.Current.Radius;

        if (_childArcParent >= 0 && _childSpans.Length > 0)
        {
            var band = radius.Ring2;
            var distance = offset.Length;
            var angle = offset.AngleDeg;

            // 子弧的角度跨度可能**超出父扇区**（2 个子项时是 90°，而父扇区只有 45°），
            // 所以这里按子弧自己的跨度判断，而不是父扇区的。
            if (distance >= band.Inner && distance <= band.Outer && _childArcSpan.Contains(angle))
            {
                var hit = RadialLayout.HitTest(offset, band.Inner, band.Outer, _childSpans);
                return hit.Zone == HitZone.Sector
                    ? new HitResult(HitZone.ChildSector, hit.Index, distance, angle)
                    // 落在子弧范围内但处在两个子扇区之间的缝隙上。
                    // 单独成一类是为了实现斜向缓冲：指针从父扇区斜着移向子弧时会经过这里，
                    // 若判成"离开"，子弧会在指针到达之前就收起。
                    : new HitResult(HitZone.ChildArcGap, -1, distance, angle);
            }
        }

        return RadialLayout.HitTest(offset, radius.DeadZone, radius.Ring1Outer, _spans);
    }
}
