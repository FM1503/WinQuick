using WinQuick.Core.Motion;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

    /// <summary>
    /// 当前子弧里各项（与 <see cref="_childSpans"/> 同序）。
    /// 留着它是为了回答"这一项自己还有子项吗" —— 点击分派据此决定
    /// "执行它"还是"再进一层"。视图不再自己去翻一级的 Children（下钻后那是错的那一层）。
    /// </summary>
    private IReadOnlyList<MenuItemSpec> _childItems = [];
    private SectorSpan _childArcSpan;
    private int _childArcParent = -1;
    private int _childAddSectorIndex = -1;
    private int _hoveredChild = -1;
    private double _childArcProgress;

    private ChildArcAnimator? _childAnimator;
    private bool _childArcRemoving;

    /// <summary>
    /// 子环的悬停形变，与一级用同一个动画器，只是几何写的是子环那一带。
    ///
    /// 子环迟了一轮才有鼓出，因为它比一级多一个变量：外径同时受**展开进度**控制。
    /// 两个写入者各写一次 Data 会互相覆盖，所以先把动画器改成由调用方写几何，
    /// 再让"展开进度 + 鼓出"在一处合成（见 <see cref="WriteChildSectorGeometry"/>）。
    /// </summary>
    private SectorBulgeAnimator? _childBulge;

    /// <summary>子环的编辑态虚线。收起子弧时要一并清掉。</summary>
    private Ellipse? _childEditOutline;

    /// <summary>
    /// 展开某个父扇区的子弧。已有子弧会立刻被替换（同时只显示一个子弧）。
    ///
    /// **子项列表由调用方按层级路径给出**（不再自己去翻一级的 `Children`）：
    /// 下钻之后环 1 是父层、子弧的父项也就可能是深层里的某一项，
    /// 视图没有"路径"这个概念之外的办法知道该取谁的子项。
    /// </summary>
    public void ExpandChildArc(int parentIndex, IReadOnlyList<MenuItemSpec> children, bool immediate = false)
    {
        if (parentIndex < 0 || parentIndex >= _spans.Length) return;
        if (children.Count == 0) return;

        if (_childArcParent == parentIndex && !_childArcRemoving)
        {
            if (immediate) SetChildArcImmediate(true);

            // 已经展开的情况也要补一次编辑态虚线：子弧可能是**日常态**下展开的，
            // 那时不加虚线，之后切到编辑态不会再走这条路径。
            SyncChildEditOutline();
            return;
        }

        RemoveChildVisuals();
        _childArcRemoving = false;
        _childArcParent = parentIndex;
        _childItems = children;
        _hoveredChild = -1;

        var tokens = TokenStore.Current;
        var radius = tokens.Radius;
        var band = radius.Ring2;
        var parentCenter = _spans[parentIndex].CenterDeg;

        // 子环同样在末尾留一个「＋」，只不过它加到的是**这个父项的子项列表**。
        // 弧跨度必须按含「＋」的个数算：ChildArc 有一条"每格至少 25°"的规则，
        // 用 children.Count 算会让多出来的那格被挤窄。
        _childAddSectorIndex =
            EditMode && children.Count < RadialLayout.MaxChildSectors ? children.Count : -1;
        var slotCount = _childAddSectorIndex >= 0 ? children.Count + 1 : children.Count;

        _childArcSpan = RadialLayout.ChildArc(
            parentCenter, slotCount, tokens.Interaction.ChildSpanDegrees);
        _childSpans = RadialLayout.DistributeWithin(_childArcSpan, slotCount);

        var colors = tokens.Color;
        var fill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        var stroke = CreateSectorStroke(colors, tokens.Radius.WindowSize);

        // 编辑态标识：子环也有自己的一圈虚线，与第一层那圈同形。
        // 用户反馈"从子环进入编辑也要像一级环一样有虚线描边" ——
        // 少了它，在子环里操作时分不清"现在编辑的是子环还是第一层"。
        // 它属于子弧的视觉，所以收进 _childExtraVisuals，收起时一并移除。
        SyncChildEditOutline();

        var iconRadius = band.At(radius.IconRadiusRatio);
        var labelRadius = band.At(radius.LabelRadiusRatio);

        // 每个子扇区自己的角度跨度差别很大（2 个子项各 45°、8 个子项各 25°），
        // 所以标签宽度必须按各自的跨度算，不能沿用第一层环的。
        var childSweep = _childArcSpan.SweepDeg / slotCount;
        var maxHalfWidth = RadiusTokens.LabelMaxHalfWidth(
            iconRadius, labelRadius, radius.IconSize, childSweep, radius.SectorGapDegrees);

        for (var i = 0; i < slotCount; i++)
        {
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

            if (i == _childAddSectorIndex)
            {
                sector.StrokeDashArray = new DoubleCollection { 3, 2.5 };

                var cross = AddCrossIcon(Root, _center, iconRadius, span.CenterDeg,
                    radius.IconSize, TokenBrushes.Brush(colors.Label));
                cross.Opacity = _childArcProgress;
                _childIcons.Add(cross);

                _childLabels.Add(new SectorLabel(
                    Root, _center, labelRadius, span.CenterDeg, AddSectorLabel,
                    tokens.Typography.LabelFontSize,
                    maxWidth: maxHalfWidth * 2,
                    maxHeight: tokens.Typography.LabelFontSize * radius.LabelLineHeightFactor * radius.LabelMaxLines,
                    lineHeightFactor: radius.LabelLineHeightFactor,
                    foreground: TokenBrushes.Brush(colors.Label, 0.8)));
                _childChildMarkers.Add(null);
                continue;
            }

            var child = children[i];
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

        // 子环的悬停鼓出。几何由 WriteChildSectorGeometry 统一写（见那里的说明），
        // 这里只负责"每个子项此刻该鼓出多少"。
        _childBulge = new SectorBulgeAnimator(
            slotCount,
            writeGeometry: WriteChildSectorGeometry,
            tokens.Motion.HoverBulgePx, tokens.Motion.EntranceEasing, tokens.Motion.HoverBulgeMs);

        if (immediate)
        {
            SetChildArcImmediate(true);
        }
        else
        {
            StartChildSlideOut(parentCenter, iconRadius, labelRadius, band.Outer);
            _childAnimator.Expand();
        }
    }

    /// <summary>
    /// 子项的**径向滑出**（用户从 preview/childring-preview.html 选定的 ③）：
    /// 每项从"父项方向、半径更小"处滑到自己的位置。
    ///
    /// 为什么这样：整条弧带一起长（原来的做法）没有方向感，而上位菜单的子项**来自**父项，
    /// 让它从父项那一侧"流"出来，等于把层级关系画了出来（Fluent 的 Connected 原则）。
    ///
    /// 只动 <c>RenderTransform</c>，不碰 <c>Canvas.Left/Top</c>（后者每帧走布局）。
    /// **淡入仍由子弧进度负责** —— 一个机制一个写入者，不叠两套（这条在本项目摔过跤）。
    /// </summary>
    private void StartChildSlideOut(double parentCenterDeg, double iconRadius, double labelRadius, double markerRadius)
    {
        var motion = TokenStore.Current.Motion;
        if (motion.ChildSlideMs <= 0 || motion.ChildSlidePx <= 0) return;

        var duration = TimeSpan.FromMilliseconds(motion.ChildSlideMs);
        var easing = motion.ChildSlideEasing;
        var slide = motion.ChildSlidePx;

        for (var i = 0; i < _childSpans.Length; i++)
        {
            var ownAngle = _childSpans[i].CenterDeg;
            // 相位差：霓虹/Material 那类皮肤靠它做"依次弹出"（0 = 一起滑，现状）。
            var delay = i * motion.ChildStaggerMs;

            SlideFromParent(_childIcons[i], iconRadius, ownAngle, parentCenterDeg, slide, duration, easing, delay);
            SlideFromParent(_childLabels[i], labelRadius, ownAngle, parentCenterDeg, slide, duration, easing, delay);

            if (i < _childChildMarkers.Count && _childChildMarkers[i] is { } marker)
                SlideFromParent(marker, markerRadius, ownAngle, parentCenterDeg, slide, duration, easing, delay);
        }
    }

    /// <summary>把一个元素从"父项方向、半径小 <paramref name="slidePx"/>"滑到它自己的位置。</summary>
    private void SlideFromParent(
        UIElement element, double ownRadius, double ownAngleDeg, double parentCenterDeg,
        double slidePx, TimeSpan duration, string easing, int delayMs)
    {
        var from = PolarOf(ownRadius - slidePx, parentCenterDeg);
        var to = PolarOf(ownRadius, ownAngleDeg);
        var dx = from.X - to.X;
        var dy = from.Y - to.Y;

        var transform = new TranslateTransform(dx, dy);
        element.RenderTransform = transform;
        var x = MotionSplines.FromTo(dx, 0, duration, easing);
        var y = MotionSplines.FromTo(dy, 0, duration, easing);
        if (delayMs > 0)
        {
            // 相位差用 BeginTime 而不是把关键帧整体后移：
            // 延迟期间元素停在**本地值**（也就是位移的起点），这正是"还没轮到我"的样子。
            x.BeginTime = y.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        }

        transform.BeginAnimation(TranslateTransform.XProperty, x);
        transform.BeginAnimation(TranslateTransform.YProperty, y);
    }

    private void SlideFromParent(
        SectorLabel label, double ownRadius, double ownAngleDeg, double parentCenterDeg,
        double slidePx, TimeSpan duration, string easing, int delayMs)
    {
        var from = PolarOf(ownRadius - slidePx, parentCenterDeg);
        var to = PolarOf(ownRadius, ownAngleDeg);
        label.SlideIn(from.X - to.X, from.Y - to.Y, duration, easing, delayMs);
    }

    /// <summary>环内的极坐标 → 窗口内坐标（与几何用的是同一套约定：0° 正上、顺时针）。</summary>
    private (double X, double Y) PolarOf(double radius, double angleDeg)
    {
        var offset = Vec2.FromPolar(radius, angleDeg);
        return (_center.X + offset.X, _center.Y + offset.Y);
    }

    /// <summary>收起子弧（带动画）。</summary>
    public void CollapseChildArc()
    {
        if (_childArcParent < 0 || _childArcRemoving) return;

        _childArcRemoving = true;
        ApplyChildSectorStyle(_hoveredChild, hovered: false);
        _hoveredChild = -1;

        if (_childAnimator is null) { CollapseChildArcImmediate(); return; }
        _childAnimator.Collapse();
    }

    /// <summary>探针接缝：子弧加载状态（有源图标数 / 图标总数 / 存活 spinner 数）。</summary>
    internal (int IconsWithSource, int TotalIcons, int SpinnersAlive) ChildArcLoadStateForProbe() =>
        (_childIcons.Count(i => i.Source is not null), _childIcons.Count,
            _childExtraVisuals.OfType<FrameworkElement>()
                .Count(e => e.Tag as string == "sector-spinner"));

    /// <summary>立即收起并移除子弧视觉，不播放动画。</summary>
    public void CollapseChildArcImmediate()
    {
        _childAnimator?.Reset();

        // 鼓出动画也必须复位并置空：它的回调写的是即将被移除的扇区，
        // 不复位就会一直挂在静态的 Rendering 事件上（既泄漏、又每帧写垃圾）；
        // 置空则是因为它绑着这一批子扇区的数量，下一轮展开要按新数量重建。
        _childBulge?.Reset();
        _childBulge = null;

        RemoveChildVisuals();
        _childArcParent = -1;
        _childArcRemoving = false;
        _hoveredChild = -1;
        _childArcProgress = 0;
        _childAddSectorIndex = -1;
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

    /// <summary>
    /// 子弧里「＋」的索引；-1 表示没有（该父项已满 12 个子项，或者当前没展开子弧）。
    /// </summary>
    public int ChildAddSectorIndex => _childAddSectorIndex;

    /// <summary>这个子弧索引是不是「＋」。</summary>
    public bool IsChildAddSector(int index) => index >= 0 && index == _childAddSectorIndex;

    /// <summary>设置子项的悬停高亮（含向外鼓出）。</summary>
    public void SetChildHover(int index)
    {
        if (index == _hoveredChild) return;

        var previous = _hoveredChild;
        _hoveredChild = index;

        // 只动变化过的那两个子项，理由同一级（见 RadialRingView.SetHover）。
        ApplyChildSectorStyle(previous, hovered: false);
        ApplyChildSectorStyle(index, hovered: true);

        // 形状与颜色分开：颜色是即时反馈，外扩是有形的位移，两者时长不同。
        _childBulge?.SetHover(index);
    }

    /// <summary>
    /// 立即把子环的悬停形变落定到终态，不播放动画。
    /// 离屏渲染要用它 —— 只渲染一帧，动画推进不了，画出来的永远是未形变的状态。
    /// </summary>
    public void SetChildHoverImmediate(int index)
    {
        SetChildHover(index);
        _childBulge?.SetHoverImmediate(index);
    }

    /// <summary>
    /// 高亮色被改（设置面板里换颜色）时重新涂一遍子弧。
    /// 平时只有两个子项会被动到，所以这里也就只需要重涂那两个。
    /// </summary>
    private void RefreshChildHighlight()
    {
        ApplyChildSectorStyle(_hoveredChild, hovered: false);
        ApplyChildSectorStyle(_hoveredChild, hovered: true);
    }

    /// <summary>把某个子项涂成悬停或基准样式。理由同一级：只动变化过的那两个。</summary>
    private void ApplyChildSectorStyle(int index, bool hovered)
    {
        if (index < 0 || index >= _childSectors.Count) return;

        var colors = TokenStore.Current.Color;

        _childSectors[index].Fill = hovered
            ? TokenBrushes.Brush(HoverFillHex, colors.HoverFillAlpha)
            : TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        _childSectors[index].Stroke = hovered
            ? TokenBrushes.Brush(colors.HoverStroke, colors.HoverStrokeAlpha)
            : CreateSectorStroke(colors, _tokens.Radius.WindowSize);

        _childLabels[index].SetHover(hovered, TokenBrushes.Brush(colors.HoverLabel));
        _childIcons[index].Opacity = hovered ? 1.0 : 0.94;
        if (_childChildMarkers[index] is { } marker)
            marker.Stroke = _childSectors[index].Stroke;
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
    /// **子环几何的唯一写入点**：外径 = 基准外径按展开进度拉开 + 该子项的鼓出量。
    ///
    /// 合成放在一处是必须的：展开动画与悬停形变是两条独立的逐帧回调，
    /// 谁都可以在任何一帧动这个扇区 —— 各写各的，后写的就把前一个的效果抹掉了。
    /// </summary>
    private void WriteChildSectorGeometry(int index, double bulgePx)
    {
        if (index < 0 || index >= _childSectors.Count) return;

        var radius = TokenStore.Current.Radius;
        var band = radius.Ring2;
        var span = _childSpans[index];

        // 弧级进度 × 该子项自己的长出进度：整条弧在张开的同时，各项按顺序一格格长满。
        var outer = band.Inner + band.Width * _childArcProgress + bulgePx;

        _childSectors[index].Data = SectorGeometry.RingSector(
            _center, band.Inner, outer,
            span.StartDeg + radius.SectorGapDegrees,
            span.EndDeg - radius.SectorGapDegrees);
    }

    /// <summary>
    /// 按当前进度重建子扇区几何：内外半径从重合（无厚度）逐渐拉开到完整环带。
    /// 这就是「从父扇区方向生长出来」这个观感的来源。
    /// </summary>
    private void RebuildChildGeometry()
    {
        for (var i = 0; i < _childSectors.Count; i++)
            WriteChildSectorGeometry(i, _childBulge?.CurrentBulgeOf(i) ?? 0);

        // 图标与文字的透明度跟随进度，否则它们会在扇区还很薄的时候先冒出来。
        var opacity = Math.Clamp(_childArcProgress, 0, 1);
        for (var i = 0; i < _childIcons.Count; i++)
        {
            if (i < _childSectors.Count) _childSectors[i].Opacity = 1.0;

            _childIcons[i].Opacity = opacity * (i == _hoveredChild ? 1.0 : 0.94);
            _childLabels[i].SetOpacity(opacity);
        }

        // spinner 与图标/标签同款跟随进度淡入淡出，否则扇区还很薄时它就实体化了。
        foreach (var s in _childExtraVisuals.OfType<FrameworkElement>())
            if (s.Tag as string == "sector-spinner") s.Opacity = opacity;
    }

    /// <summary>按当前编辑态增删子环那圈虚线。可重复调用。</summary>
    private void SyncChildEditOutline()
    {
        if (!EditMode || _childArcParent < 0)
        {
            if (_childEditOutline is not null)
            {
                Root.Children.Remove(_childEditOutline);
                _childExtraVisuals.Remove(_childEditOutline);
                _childEditOutline = null;
            }
            return;
        }

        if (_childEditOutline is not null) return;

        var radius = TokenStore.Current.Radius;
        var band = radius.Ring2;

        var outline = new Ellipse
        {
            Width = (band.Outer + 6) * 2,
            Height = (band.Outer + 6) * 2,
            Fill = null,
            Stroke = TokenBrushes.Brush(TokenStore.Current.Color.HoverStroke, 0.5),
            StrokeThickness = 1.4,
            StrokeDashArray = new DoubleCollection { 3, 4 },
            IsHitTestVisible = false,
            Opacity = _childArcProgress,
        };
        Canvas.SetLeft(outline, _center.X - (band.Outer + 6));
        Canvas.SetTop(outline, _center.Y - (band.Outer + 6));
        Root.Children.Add(outline);
        _childExtraVisuals.Add(outline);
        _childEditOutline = outline;
    }

    private void RemoveChildVisuals()
    {
        // 逐项长出的回调必须在清空数组**之前**摘掉，否则它会去写已经清空的集合。

        // 子环都没了，针对它的拖拽预览必须一起作废：否则下一帧的 UpdateDrag
        // 会去访问已经清空的子环扇区数组，直接越界崩溃
        //（收起动画可能在拖拽进行中才播完 —— 它是在按下之前就被排上的）。
        if (_dragIsChild) ClearDragState();

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
        _childItems = [];
        _childEditOutline = null;
    }

    private Image AddChildIcon(MenuItemSpec child, double iconRadius, double angleDeg, double iconSize)
    {
        var image = new Image { Width = iconSize, Height = iconSize };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Opacity = _childArcProgress;

        var offset = Vec2.FromPolar(iconRadius, angleDeg);

        UIElement? spinner = null;
        var source = ShellIcons.GetOrBeginGet(child.IconPath, ready =>
        {
            // 回调已回到 UI 线程；但子弧可能已收起/重建 —— 元素离树就丢弃，
            // 旧树的视觉由旧树自己的生命周期负责（RemoveChildVisuals）。
            if (image.Parent is null) return;
            if (ready is not null)
            {
                image.Source = ready;
                if (spinner is { } s)
                {
                    _childExtraVisuals.Remove(s);
                    Root.Children.Remove(s);
                }
            }
            else
            {
                // 抽取失败：spinner 换成首字占位（与「无图标路径」同一落点）。
                if (spinner is { } s)
                {
                    _childExtraVisuals.Remove(s);
                    Root.Children.Remove(s);
                }
                AddPlaceholder(Root, _center, iconRadius, angleDeg, child.Label, iconSize,
                    _childExtraVisuals);
            }
        }, out var pending);
        if (source is not null)
        {
            image.Source = source;
        }
        else if (!pending)
        {
            // 已定且无图标（无路径，或此前抽取失败被负缓存）：画首字占位，而不是 spinner 永久转。
            AddPlaceholder(Root, _center, iconRadius, angleDeg, child.Label, iconSize,
                _childExtraVisuals);
        }
        else
        {
            // 有路径但未就绪：环形加载占位（spec §2.3 档②），就绪即换。
            spinner = SectorSpinner.Create(iconSize, TokenBrushes.Brush(TokenStore.Current.Color.Label));
            _childExtraVisuals.Add(spinner);
            Root.Children.Add(spinner);
            spinner.Opacity = _childArcProgress;
            Canvas.SetLeft(spinner, _center.X + offset.X - iconSize / 2);
            Canvas.SetTop(spinner, _center.Y + offset.Y - iconSize / 2);
            Panel.SetZIndex(spinner, 0);
        }
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
