using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinQuick.App.Design;
using WinQuick.App.Interop;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using WinQuick.Core.Interaction;
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
/// <summary>
/// 轮盘视图。子弧（第二层）的渲染与命中测试在
/// <c>RadialRingView.ChildArc.cs</c> 里，用 partial 拆开以免主文件无限膨胀。
/// </summary>
public sealed partial class RadialRingView
{
    /// <summary>
    /// 当前生效的规格。**可变**：换皮肤时就地更新（见 <see cref="TryApplySkinInPlace"/>），
    /// 那时不重建视觉树。
    /// </summary>
    private DesignTokens _tokens;
    private readonly Point _center;
    private readonly SectorSpan[] _spans;
    private readonly ShapePath[] _sectors;
    private readonly SectorLabel[] _labels;
    private readonly Image[] _icons;
    private readonly ShapePath?[] _childMarkers;
    /// <summary>
    /// 悬停鼓出。**在视图构造之后立刻由 Build 补上** —— 它的回调要指向视图的几何写入点
    /// （内缘不动、外缘 = 基准外缘按入场进度拉开 + 鼓出），而那个写入点是视图的方法，
    /// 两者互相依赖（子弧那边也是这么建的）。
    /// </summary>
    private SectorBulgeAnimator _bulge = null!;

    /// <summary>冻结的基准几何（无鼓出、入场已落定的样子）。静止时直接复用它，逐像素可复现。</summary>
    private readonly PathGeometry[] _baseGeometry;

    /// <summary>当前形态。决定单元外形、悬停反馈方式，以及要不要画刻度与指针。</summary>
    private RingForm _form = RingForm.Pie;

    /// <summary>光晕层（皮肤没配光晕时为 null）。与扇区共用一份几何，逐帧同步。</summary>
    private ShapePath[]? _halos;

    /// <summary>刻度盘的指针（旋转到悬停项的角度）。非刻度盘形态为 null。</summary>
    private UIElement? _needle;

    /// <summary>中心死区圆与编辑态虚线圈 —— 就地换材质时要改它们的画刷。</summary>
    private Shape? _deadZone;
    private Shape? _editRing;

    /// <summary>
    /// <summary>菜单定义（层级路径要拿它取每一层的项）。</summary>
    private readonly MenuSpec _menu;

    /// <summary>
    /// 当前显示到哪一级。**环 1 画的不是固定的一级**，而是这条路径的父层 ——
    /// 「旧的环 2 搬到环 1」这句话在视图层就是"环 1 的项来自 <see cref="LevelPath.Ring1Level"/> 层"。
    /// </summary>
    private readonly LevelPath _path;

    /// <summary>环 1 当前要画的项（<see cref="_path"/> 决定，构建时取一次）。</summary>
    private readonly IReadOnlyList<MenuItemSpec> _ring1Items;

    /// <summary>
    /// 环 1 里"没有图标"的项画出来的占位图（圆圈 + 首字，每项两个元素）。
    /// 它们由 AddPlaceholder 直接加进画布、不在扇区/图标数组里，所以必须单独登记：
    /// 层级位移时只搬登记过的元素的话，这些项会留在原地不动（只有边框在变形）。
    /// </summary>
    private readonly List<UIElement> _ring1Placeholders;

    private int _hover = -1;

    /// <summary>
    /// 高亮色的用户覆盖值（#RRGGBB）。null 表示用设计基线。
    /// 设置面板改色时要立刻可见，所以这里必须能给视图一个新值，
    /// 而不是等重建窗口 —— 重建会让"改动即预览"这件事断掉。
    /// </summary>
    private string? _accentOverride;

    /// <summary>当前生效的高亮色（用户覆盖优先，见 <see cref="_accentOverride"/>）。</summary>
    private string HoverFillHex => _accentOverride ?? _tokens.Color.HoverFill;

    /// <summary>更新高亮色并立即重绘当前悬停状态。</summary>
    public void SetAccentOverride(string? hex)
    {
        if (string.Equals(_accentOverride, hex, StringComparison.OrdinalIgnoreCase)) return;

        _accentOverride = hex;

        var previous = _hover;
        _hover = -2;              // 强制 SetHover 走一遍（它靠"索引没变就提前返回"）
        SetHover(previous);

        // 子弧里的高亮用的是同一个强调色，也要跟着换。
        RefreshChildHighlight();
    }

    internal RadialRingView(
        Canvas root, DesignTokens tokens, Point center, SectorSpan[] spans,
        ShapePath[] sectors, PathGeometry[] baseGeometry,
        SectorLabel[] labels, Image[] icons, ShapePath?[] childMarkers,
        MenuSpec menu, LevelPath path, List<UIElement> ring1Placeholders)
    {
        Root = root;
        _menu = menu;
        _path = path;
        _ring1Items = path.Ring1Items(menu);
        _ring1Placeholders = ring1Placeholders;
        _tokens = tokens;
        _center = center;
        _spans = spans;
        _sectors = sectors;
        _baseGeometry = baseGeometry;
        _labels = labels;
        _icons = icons;
        _childMarkers = childMarkers;
    }

    /// <summary>视觉根。类型是 Canvas 而不是 FrameworkElement —— 子弧需要往它里面增删图形。</summary>
    public Canvas Root { get; }

    /// <summary>圆心（窗口内坐标）。探针挂粒子场时要用。</summary>
    public Point Center => _center;

    public int SectorCount => _sectors.Length;
    public int HoverIndex => _hover;

    /// <summary>
    /// 供离屏预览与探针核对「图标是否真的画出来了」，不信任视觉判断。
    /// 只数真实菜单项 —— 「＋」也是个 Image（画的是加号，不是 shell 图标），
    /// 把它算进去会让这个计数在探针里永远是 itemCount+1，失去核对意义。
    /// </summary>
    /// <summary>
    /// 诊断用：中心的 "Esc" 提示是否可见。
    ///
    /// 它必须**始终**可见：曾经有一版会在悬停到被截断的项时把它换成完整名字，
    /// 那是错的 —— Esc 是界面上唯一的取消提示，不该因为某个项名字长就消失。
    /// </summary>
    internal bool EscHintVisible =>
        _escHint.Visibility == Visibility.Visible
        || _centerLabels.Any(l => l.Visibility == Visibility.Visible);

    /// <summary>
    /// 诊断用：右半「上一层」标签的透明度（0–1；-1 = 没有这个标签）。
    /// 锁两件事：第一层时它必须明显比左半暗（灰态），
    /// 以及**换肤就地重刷之后灰不丢**（第一版就在那里把两个标签统一刷成亮色）。
    /// </summary>
    internal double CenterBackAlphaForProbe =>
        _centerBackLabel?.Foreground is SolidColorBrush b ? b.Color.A / 255.0 : -1;

    /// <summary>中心的 "Esc" 提示。它任何时候都不该被隐藏或替换。</summary>
    private TextBlock _escHint = null!;

    /// <summary>中心两半的文字（左 Esc、右 上一层）。编辑态下为空。</summary>
    private List<TextBlock> _centerLabels = [];

    /// <summary>右半「上一层」的标签 —— 它有灰态，换肤时要按角色重刷（见 TryApplySkinInPlace）。</summary>
    private TextBlock? _centerBackLabel;

    /// <summary>右半当前是否可用（不可用 = 画灰）。换肤重刷时要按它决定透明度。</summary>
    private bool _centerBackAvailable;

    /// <summary>中缝分隔线 —— 换肤时要重刷它的颜色。</summary>
    private Line? _centerDivider;

    /// <summary>灰态文字的透明度系数（乘在 hintAlpha 上）。</summary>
    private const double DimmedHintFactor = 0.4;

    /// <summary>诊断用：某个标签的排版快照。</summary>
    internal (double Left, double FontSize, string Weight, bool Wrapping, double Width)
        LabelLayoutSnapshot(int index) =>
        index >= 0 && index < _labels.Length ? _labels[index].LayoutSnapshot : default;

    /// <summary>诊断用：某个标签是否挂着动画时钟。</summary>
    internal bool LabelHasAnimation(int index) =>
        index >= 0 && index < _labels.Length && _labels[index].HasActiveAnimation;

    /// <summary>诊断用：某个标签的完整文字。</summary>
    internal string LabelText(int index) =>
        index >= 0 && index < _labels.Length ? _labels[index].Text : "";

    /// <summary>诊断用：某个标签是否被截断。</summary>
    internal bool LabelIsElided(int index) =>
        index >= 0 && index < _labels.Length && _labels[index].IsElided;

    /// <summary>诊断用：某个标签的内容是否真的装进了框里。</summary>
    internal bool LabelFitsBox(int index) =>
        index >= 0 && index < _labels.Length && _labels[index].FitsBox;

    /// <summary>诊断用：有没有标签"既没装下、又没标出截断"（会被悄悄裁掉）。</summary>
    internal IReadOnlyList<string> SilentlyClippedLabels =>
        _labels.Where(l => !l.FitsBox && !l.IsElided).Select(l => l.FullText).ToList();

    /// <summary>诊断用：某个标签的最终字号。</summary>
    internal double LabelFontSize(int index) =>
        index >= 0 && index < _labels.Length ? _labels[index].FontSize : 0;

    /// <summary>诊断用：某个标签的文字占宽。</summary>
    internal double LabelTextWidth(int index) =>
        index >= 0 && index < _labels.Length ? _labels[index].TextWidth : 0;

    public int RenderedIconCount
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _icons.Length; i++)
                if (!IsAddSector(i) && _icons[i].Source is not null) n++;
            return n;
        }
    }

    public int ChildMarkerCount => _childMarkers.Count(m => m is not null);

    /// <summary>需要滚动才能看全的标签数 —— 供验证「长文本是否被妥善处理」。</summary>
    public int ElidedLabelCount => _labels.Count(l => l.IsElided);

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

        var previous = _hover;
        _hover = index;

        // **只改变化过的那两个扇区**，不遍历全环。
        //
        // 原来这里是 for 循环扫一遍所有扇区、把所有属性重新赋一遍。属性值赋成相同的
        // 值不会触发重绘，所以旧的写法"结果正确"，但每次悬停切换都要走 N 次
        // 画刷查找与属性写入 —— 悬停是轮盘上最频繁的动作，这些白做的活会直接
        // 变成掉帧。两个索引之外的扇区本来就处于基准样式，不需要被动。
        ApplySectorStyle(previous, hovered: false);
        ApplySectorStyle(index, hovered: true);

        // 几何形变交给逐帧动画器：颜色高亮是即时反馈，外扩是有形的位移，
        // 两者时长不同，所以分开处理。
        _bulge.SetHover(index);
    }

    /// <summary>把某个扇区涂成悬停或基准样式。越界索引会被忽略（-1 就是"没有悬停"）。</summary>
    private void ApplySectorStyle(int index, bool hovered)
    {
        if (index < 0 || index >= _sectors.Length) return;

        var colors = _tokens.Color;

        _sectors[index].Fill = hovered
            ? TokenBrushes.Brush(HoverFillHex, colors.HoverFillAlpha)
            : TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        _sectors[index].Stroke = hovered
            ? TokenBrushes.Brush(colors.HoverStroke, colors.HoverStrokeAlpha)
            : CreateSectorStroke(colors, _tokens.Radius.WindowSize);

        // 悬停时的文字色单独一个令牌：纯色块（黄/红）当底时必须换成深色字，
        // 否则读不出来 —— 这条有校验锁着（亮度差不足会被 Validate 拦下）。
        _labels[index].SetHover(hovered, TokenBrushes.Brush(colors.HoverLabel));

        // 图标与子菜单标记跟着扇区明暗走，否则高亮时它们会像浮在错误的层上。
        _icons[index].Opacity = hovered ? 1.0 : 0.94;
        if (_childMarkers[index] is { } marker)
            marker.Stroke = _sectors[index].Stroke;

        // 刻度盘的指针指向被悬停的那一格。
        if (hovered && _needle is not null)
            _needle.RenderTransform = new RotateTransform(_spans[index].CenterDeg, _center.X, _center.Y);
    }

    /// <summary>当前形态（换皮肤时用来判断能不能就地更新）。</summary>
    public RingForm Form => _form;

    /// <summary>
    /// 诊断用：某一格当前的填充色与描边色（16 进制，含 alpha）。
    ///
    /// 为什么需要它：换皮肤现在有**两条路径**（就地换材质 / 重建），
    /// 而"就地"必须在功能上等价于重建 —— 否则会出现"点了皮肤没变"这种只有肉眼才发现的 bug。
    /// 有了这个读数，探针就能断言"换皮肤之后这一格的填色确实变成了新规格里的值"。
    /// </summary>
    internal (string Fill, string Stroke, double StrokeWidth) SectorStyleForProbe(int index)
    {
        if (index < 0 || index >= _sectors.Length) return ("", "", 0);
        return (_sectors[index].Fill?.ToString() ?? "",
                _sectors[index].Stroke?.ToString() ?? "",
                _sectors[index].StrokeThickness);
    }

    /// <summary>
    /// **就地换材质**：把新规格里的颜色/描边宽度/光晕套到已有元素上，**不重建视觉树**。
    ///
    /// 为什么要它（实测）：换一次皮肤如果重建整棵树，代价不只是分配 ——
    /// 重建后的内容要重新渲染进分层窗口，实测**每次重建净留约 0.65MB**，
    /// 40 次重建 +26MB，强制回收也只回来一半。用户点几下皮肤就能把常驻内存顶上去。
    /// 就地换材质一个元素都不新建，就不会有这笔累积。
    ///
    /// 返回 false 表示"这次换不了，请重建"。会返回 false 的只有两种情况：
    /// ① 光晕的有无变了（多一层几何，必须重建）；② 调用方传入的形态与当前不同（几何要重画）。
    /// </summary>
    public bool TryApplySkinInPlace(RingForm form)
    {
        if (form != _form) return false;

        var next = TokenStore.Current;
        var colors = next.Color;

        // 光晕的有无变了 → 多/少一层几何，只能重建。
        if ((colors.HaloPx > 0) != (_halos is not null)) return false;

        // **环的几何参数变了也要重建**（扇区形状/位置会变）。
        // 少这条守卫的话，就得每次换皮肤都重算 12 个 PathGeometry —— 而那正是
        // 内存累积的来源：**新几何 = 新渲染内容 = 新的渲染数据**。
        // 实测：只改画刷不重算几何，才会真的不累积。
        if (!SameRingGeometry(_tokens.Radius, next.Radius)) return false;

        _tokens = next;

        var baseFill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        var baseStroke = CreateSectorStroke(colors, _tokens.Radius.WindowSize);
        var haloBrush = colors.HaloPx > 0
            ? TokenBrushes.Brush(colors.HaloColor, colors.HaloAlpha)
            : null;

        for (var i = 0; i < _sectors.Length; i++)
        {
            _sectors[i].StrokeThickness = colors.StrokeWidth;
            _sectors[i].Fill = baseFill;
            _sectors[i].Stroke = baseStroke;
            if (_halos is not null && haloBrush is not null)
            {
                _halos[i].Stroke = haloBrush;
                _halos[i].StrokeThickness = colors.HaloPx;
            }
            _labels[i].SetBaseForeground(TokenBrushes.Brush(colors.Label));
            _childMarkers[i]?.Let(m => m.Stroke = baseStroke);
        }

        if (_deadZone is not null)
        {
            _deadZone.Fill = TokenBrushes.Brush(colors.DeadZoneFill);
            _deadZone.Stroke = TokenBrushes.Brush(colors.DeadZoneStroke, colors.DeadZoneStrokeAlpha);
        }
        if (_editRing is not null)
            _editRing.Stroke = TokenBrushes.Brush(colors.HoverStroke, 0.5);

        // 中心的三处文字/线条：编辑态那一个词、左右两半的标签（左 Esc、右 上一层）、中缝。
        // 它们用的是 Hint 色，换肤要跟着变 —— 但**右半的灰态必须保留**：
        // 第一版在这里把两个标签统一刷成亮色，用户存的皮肤在启动时走一次就地换肤，
        // 「上一层」的灰就被冲掉了（表现为"第一层时右边没置灰"）。所以按角色各自重刷。
        _escHint.Foreground = TokenBrushes.Brush(colors.Hint, colors.HintAlpha);
        if (_centerBackLabel is not null)
            _centerBackLabel.Foreground = TokenBrushes.Brush(colors.Hint,
                _centerBackAvailable ? colors.HintAlpha : colors.HintAlpha * DimmedHintFactor);
        if (_centerDivider is not null)
            _centerDivider.Stroke = TokenBrushes.Brush(
                colors.DeadZoneStroke, colors.DeadZoneStrokeAlpha);

        // 悬停态的颜色也要按新规格重上一遍（走"强制重算"那条路：索引没变时 SetHover 会提前返回）。
        var previous = _hover;
        _hover = -2;
        SetHover(previous);
        RefreshChildHighlight();

        return true;
    }

    /// <summary>
    /// 扇区描边的唯一来源（skin-revamp fix1）：平色或玻璃拟态渐变（strokeGradient）。
    /// 全部赋值点（构造/就地换肤/悬停恢复/子环）都必须走这里 ——
    /// 曾只接一条路径导致重启、重建、悬停过后、子环的渐变静默丢失（reviewer P1）。
    /// 静态 + 窗口高参数：Build/AddPlaceholder 等静态构造路径也要同一来源。
    /// </summary>
    private static Brush CreateSectorStroke(ColorTokens colors, double windowSize)
    {
        var stroke = TokenBrushes.Brush(colors.SectorStroke, colors.SectorStrokeAlpha);
        if (!colors.StrokeGradient) return stroke;

        // 整窗垂直渐变：上亮下暗。MappingMode 必须 Absolute——相对 boundingBox 会让
        // 顶部/底部扇区各自截到渐变的不同段。窗口高读令牌（预览 sizeOverride 场景才不会错位）。
        var gradient = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, windowSize),
        };
        gradient.GradientStops.Add(new GradientStop(
            TokenBrushes.Parse(colors.SectorStroke, colors.SectorStrokeAlpha), 0));
        gradient.GradientStops.Add(new GradientStop(
            TokenBrushes.Parse(colors.SectorStroke, colors.SectorStrokeAlpha * 0.24), 1));
        gradient.Freeze();
        return gradient;
    }

    /// <summary>
    /// 两次规格里"决定**一级环**形状"的参数是否一致（一致才能只换画刷）。
    ///
    /// **刻意不比较 ring2Inner / ring2Outer**：那两条只影响子环，而子环的几何是在
    /// **展开那一刻**按当时的令牌现算的（见 ChildArc 里的写入点），所以它们变了也不影响
    /// 一级环的现有几何 —— 比较它们只会白白把"霓虹/材质"这类皮肤挡在就地更新之外，
    /// 让用户每点一次就付一次整树重建（实测 0.65MB 的累积）。
    /// </summary>
    private static bool SameRingGeometry(RadiusTokens a, RadiusTokens b) =>
        a.DeadZone == b.DeadZone &&
        a.Ring1Outer == b.Ring1Outer &&
        Math.Abs(a.SectorGapDegrees - b.SectorGapDegrees) < 0.001 &&
        a.IconSize == b.IconSize &&
        Math.Abs(a.IconRadiusRatio - b.IconRadiusRatio) < 0.001 &&
        Math.Abs(a.LabelRadiusRatio - b.LabelRadiusRatio) < 0.001;

    /// <summary>
    /// 把窗口内的坐标交给 Core 的命中测试，返回落在哪个区域/扇区。
    /// 子弧环带优先于第一层环（实现在 partial 文件里）。
    /// </summary>
    public HitResult HitTest(Point localPoint) =>
        HitTestWithChildArc(new Vec2(localPoint.X - _center.X, localPoint.Y - _center.Y));

    // ---------------------------------------------------------------- 构造

    /// <param name="editMode">
    /// 编辑态。只在编辑态下于环末尾画出「＋」扇区 ——
    /// 日常使用时轮盘里应当只有用户自己的项，编辑入口属于设置场景。
    /// 传 false 时即使有空位也不画「＋」，各扇区的角度随之回到完整等分。
    /// </param>
    /// <param name="reuse">
    /// 复用一棵已有的画布（把它的子元素清空后重建内容）。
    ///
    /// **菜单重建时必须传它。** 换掉 `Window.Content` 会让窗口的整棵内容失效、
    /// 走一次完整的窗口级测量/排列，在分层窗口上就会闪一下 ——
    /// 而用户改一次菜单就会重建一次，看起来就是"刷新时频繁闪"。
    /// 复用同一个画布则只失效它的子元素，不触碰 Window 的内容对象。
    /// </param>
    public static RadialRingView Build(
        MenuSpec menu, double? sizeOverride = null, bool editMode = false, Canvas? reuse = null,
        RingForm form = RingForm.Pie, LevelPath? path = null)
    {
        var tokens = TokenStore.Current;
        var radius = tokens.Radius;
        var size = sizeOverride ?? radius.WindowSize;
        var center = new Point(size / 2, size / 2);
        var level = path ?? LevelPath.AtRoot;

        // 环 1 画的是**路径决定的父层**：深度 0 时就是一级，更深时是上一层
        //（下钻时"旧的环 2 整层搬到环 1"就落在这一行）。
        var ring1Items = level.Ring1Items(menu);

        // 形态决定单元的外形与占位：花瓣内侧退一点、刻度盘外缘收一点给刻度让位。
        // 命中测试仍然用 RadialLayout 的角度区间 —— 四种形态共用同一套区间。
        var metrics = RingForms.Metrics(form, radius.DeadZone, radius.Ring1Outer, radius.SectorGapDegrees);

        var root = reuse ?? new Canvas();
        root.Children.Clear();
        root.Width = size;
        root.Height = size;

        // Transparent（而非 null）才可命中测试：整窗范围都能收到鼠标事件，
        // 「点圆盘外关闭」才成立。
        root.Background = Brushes.Transparent;

        // 所有角度都走 Core 的 RadialLayout：渲染与命中测试共用同一份数学，
        // 不会出现「看着在扇区 A、点下去选中 B」。
        // 末尾多留一个扇区画「＋」。到上限就不再显示它 —— 12 项时环已经满了，
        // 再加一个只会让每个扇区细到点不中。
        //
        // 为什么是"排在末尾"而不是"固定在某个角度"：角度分配只有一套规则（均匀分布），
        // 引入一个固定位置的特殊扇区就等于有了两套角度映射，用户更难预判。
        var itemCount = ring1Items.Count;
        var addIndex = editMode && itemCount < RadialLayout.MaxSectors ? itemCount : -1;
        var count = addIndex >= 0 ? itemCount + 1 : itemCount;

        var spans = RadialLayout.Distribute(count);

        var sectors = new ShapePath[count];
        var baseGeometry = new PathGeometry[count];
        var labels = new SectorLabel[count];
        var icons = new Image[count];
        var childMarkers = new ShapePath?[count];
        var ring1Placeholders = new List<UIElement>();

        var colors = tokens.Color;
        var fill = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
        var stroke = CreateSectorStroke(colors, radius.WindowSize);

        // 光晕：垫在扇区下面的一圈更粗描边（0 = 不画，绝大多数皮肤是 0）。
        // 用几何而不是 WPF Effect —— Effect 让元素每帧走位图渲染，代价与面积相关。
        // 数组一旦建立就**每一格都有值**（类型是非空数组），省得逐帧回调里还要判空。
        var hasHalo = colors.HaloPx > 0;
        var halos = hasHalo ? new ShapePath[count] : null;
        var haloBrush = hasHalo ? TokenBrushes.Brush(colors.HaloColor, colors.HaloAlpha) : null;

        for (var i = 0; i < count; i++)
        {
            var span = spans[i];

            var geometry = SectorGeometry.ForForm(
                form, center, metrics.Inner, metrics.Outer,
                span.StartDeg + metrics.GapDegrees,
                span.EndDeg - metrics.GapDegrees,
                metrics.CornerRadius);

            if (halos is not null)
            {
                var halo = new ShapePath
                {
                    Data = geometry,
                    Fill = null,
                    Stroke = haloBrush,
                    StrokeThickness = colors.HaloPx,
                    IsHitTestVisible = false,
                };
                halos[i] = halo;
                root.Children.Add(halo);          // 先加光晕，扇区盖在它上面
            }

            var sector = new ShapePath
            {
                Data = geometry,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = colors.StrokeWidth,
            };
            sectors[i] = sector;
            baseGeometry[i] = geometry;
            root.Children.Add(sector);

            // 标签宽度必须按**所有**扇区（含"添加"）算，因为它取的是最窄的那个 ——
            // 用 itemCount 会算出偏宽的标签框，在 9/10 扇区时和图标的水平距离不够。
            var labelMaxWidth = RadiusTokens.LabelMaxHalfWidth(radius, count) * 2;
            var labelMaxHeight =
                tokens.Typography.LabelFontSize * radius.LabelLineHeightFactor * radius.LabelMaxLines;

            if (i == addIndex)
            {
                // 「＋」是一个操作而不是一个菜单项：虚线描边把它和真实项区分开。
                sector.StrokeDashArray = new DoubleCollection { 3, 2.5 };
                icons[i] = AddCrossIcon(root, center, radius.IconRadius, span.CenterDeg,
                    radius.IconSize, TokenBrushes.Brush(colors.Label));
                labels[i] = new SectorLabel(
                    root, center, radius.LabelRadius, span.CenterDeg, AddSectorLabel,
                    tokens.Typography.LabelFontSize,
                    maxWidth: labelMaxWidth, maxHeight: labelMaxHeight,
                    lineHeightFactor: radius.LabelLineHeightFactor,
                    foreground: TokenBrushes.Brush(colors.Label, 0.8));
                continue;
            }

            var item = ring1Items[i];
            icons[i] = AddIcon(root, center, radius.IconRadius, span.CenterDeg, item, radius.IconSize,
                ring1Placeholders);
            labels[i] = new SectorLabel(
                root, center, radius.LabelRadius, span.CenterDeg, item.Label,
                tokens.Typography.LabelFontSize,
                maxWidth: labelMaxWidth, maxHeight: labelMaxHeight,
                lineHeightFactor: radius.LabelLineHeightFactor,
                foreground: TokenBrushes.Brush(colors.Label));

            if (item.HasChildren)
                childMarkers[i] = AddChildMarker(root, center, radius.Ring1Outer, span.CenterDeg, stroke);
        }

        // 刻度盘的装饰层：60 条刻度 + 一根指向悬停项的指针。
        //
        // 两者都**不参与命中测试**（IsHitTestVisible = false）—— 它们是装饰，
        // 指着刻度点下去不该有任何反应。指针的角度由悬停状态驱动（见 SetHover）。
        var needle = form == RingForm.Dial ? AddDialDecor(root, center, radius, tokens, stroke) : null;

        // 编辑态标识：环外一圈**持续显示**的虚线。
        //
        // 为什么需要它：用户反馈"松开鼠标就以为退出编辑态了"。行为上编辑态其实一直在
        //（只有 Esc / 点中心 / 点环外才退），但屏幕上没有持久的标记 ——
        // 被拖那一项的"抬起"和虚线目标框一松手就消失，看起来就像模式结束了。
        // 加一个不随拖拽起落变化的标识，模式在不在就一目了然。
        Ellipse? editRing = null;
        if (editMode)
        {
            editRing = new Ellipse
            {
                Width = (radius.Ring1Outer + 8) * 2,
                Height = (radius.Ring1Outer + 8) * 2,
                Fill = null,
                Stroke = TokenBrushes.Brush(colors.HoverStroke, 0.5),
                StrokeThickness = 1.4,
                StrokeDashArray = new DoubleCollection { 3, 4 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(editRing, center.X - (radius.Ring1Outer + 8));
            Canvas.SetTop(editRing, center.Y - (radius.Ring1Outer + 8));
            root.Children.Add(editRing);
        }

        // 中心死区：分成两半 —— **左半 = Esc（关闭），右半 = 返回上一层**
        //（用户指定的分工；样式是每半一行字，见 AddCenterParts）。
        //
        // 底下那个内凹的圆照旧画着：它既是"这里不是可选项"的视觉暗示，
        // 也是皮肤就地切换时要改画刷的那个元素（_deadZone）。
        var deadZone = AddEllipse(root, center, radius.DeadZone - 4,
            TokenBrushes.Brush(colors.DeadZoneFill),
            TokenBrushes.Brush(colors.DeadZoneStroke, colors.DeadZoneStrokeAlpha), 1);

        var centerParts = AddCenterParts(root, center, radius, tokens, level.Depth, editMode);

        var view = new RadialRingView(
            root, tokens, center, spans, sectors, baseGeometry, labels, icons, childMarkers,
            menu, level, ring1Placeholders)
        {
            AddSectorIndex = addIndex,
            EditMode = editMode,
            _escHint = centerParts.Hint,
            _centerLabels = centerParts.ZoneLabels,
            _centerBackLabel = centerParts.Back,
            _centerBackAvailable = !editMode && CenterZones.IsBackAvailable(level.Depth),
            _centerDivider = centerParts.Divider,
            _deadZone = deadZone,
            _editRing = editRing,
            _form = form,
            _halos = halos,
            _needle = needle,
        };

        // 鼓出动画器在视图建好之后才建：它的回调必须指向视图的几何写入点
        //（那里把悬停鼓出与逐格入场进度合成在一处，见 WriteRingSectorGeometry）。
        view._bulge = new SectorBulgeAnimator(
            count, view.WriteRingSectorGeometry,
            tokens.Motion.HoverBulgePx, tokens.Motion.EntranceEasing, tokens.Motion.HoverBulgeMs);

        return view;
    }

    /// <summary>
    /// **环上几何的唯一写入点**：内缘（死区）不动，外缘按鼓出量外扩。
    /// 合成放在一处是必须的：这是逐帧回调唯一被允许改扇区几何的地方。
    ///
    /// 静止时直接用**冻结的基准几何**：既省下每帧重建，
    /// 也让"静止态逐像素可复现"这条不变量成立（--perf-probe 锁着它）。
    /// </summary>
    private void WriteRingSectorGeometry(int index, double bulgePx)
    {
        if (index < 0 || index >= _sectors.Length) return;

        // 非饼形：反馈做成**沿半径方向的抬起**（位移 Transform），不重建几何。
        //
        // 为什么两种做法并存：饼形扇区的外扩是"形状本身变了"（外缘半径增大），
        // 而圆角/花瓣/刻度盘的单元更像一块块独立的牌子，"整块往外挪"才是它们的语汇。
        // 抬起走 Transform，是纯渲染变换，连亚像素都省了。
        if (_form != RingForm.Pie)
        {
            ApplyRingLift(index, bulgePx);
            return;
        }

        if (bulgePx < 0.05)
        {
            _sectors[index].Data = _baseGeometry[index];
            if (_halos is not null) _halos[index].Data = _baseGeometry[index];
            return;
        }

        var radius = _tokens.Radius;
        var span = _spans[index];
        var outer = radius.Ring1Outer + bulgePx;

        // 走 RingSector（新建 + 冻结）而**不是**就地改。
        //
        // 试过就地改（同一个 PathGeometry 逐帧改点，省下每帧 5 个对象的分配）：分配降了约 15%，
        // 但帧分布没有可测差异（8/14/11 对 13/15/13，在噪声内），而可变的几何与冻结几何的
        // 光栅化结果不一致（亚像素级）—— 于是"换菜单后画面与之前逐像素相同"这类判据会间歇变红。
        // 收益测不出来、代价是像素不可复现、还多了一种几何形态，所以回退。
        var geometry = SectorGeometry.RingSector(
            _center, radius.DeadZone, outer,
            span.StartDeg + radius.SectorGapDegrees,
            span.EndDeg - radius.SectorGapDegrees);

        _sectors[index].Data = geometry;
        // 光晕是紧贴扇区的一圈更粗的描边，几何必须与扇区**同步**外扩，
        // 否则悬停时扇区动、光晕不动，看起来像有两层没对齐的边。
        if (_halos is not null) _halos[index].Data = geometry;
    }

    /// <summary>
    /// 把某一格沿它的半径方向抬起 <paramref name="px"/> 像素（非饼形形态的悬停反馈）。
    /// 扇区、图标、文字都要抬，否则文字会留在原地、看起来像从扇区上滑下去。
    /// </summary>
    private void ApplyRingLift(int index, double px)
    {
        var offset = Vec2.FromPolar(px, _spans[index].CenterDeg);
        var transform = new TranslateTransform(offset.X, offset.Y);

        _sectors[index].RenderTransform = transform;
        _icons[index].RenderTransform = transform;
        _labels[index].SetLift(offset.X, offset.Y);
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
        // 拖拽预览也要收掉：残留的目标格子标记会留在视觉树上。
        EndDrag();
        // 层级位移的逐帧回调也要停：它挂在静态事件 CompositionTarget.Rendering 上，
        // 留着这个旧树既不被回收、又每帧都在改几何。
        ResetLevelShift();
        _bulge.Reset();
        CollapseChildArcImmediate();
        _hover = -1;
    }

    /// <summary>
    /// 视觉树上的直接子元素数量。用于核对展开/收起一轮之后有没有残留 ——
    /// 「每次收起都留下几个元素」这类泄漏在性能劣化前完全不可见。
    /// </summary>
    public int VisualChildCount => Root.Children.Count;

    /// <summary>
    /// 「＋」扇区的索引；-1 表示没有（菜单已满 12 项）。
    /// 它排在所有真实项之后，因此 <c>index &lt; AddSectorIndex</c> 的索引可以直接当菜单项索引用。
    /// </summary>
    public int AddSectorIndex { get; private init; } = -1;

    /// <summary>这个索引是不是「＋」扇区。</summary>
    public bool IsAddSector(int index) => index >= 0 && index == AddSectorIndex;

    /// <summary>「＋」扇区的文字。命中它时按这个报给上层。</summary>
    public const string AddSectorLabel = "添加";


    /// <summary>这一份视图是否处于编辑态（决定「＋」扇区画不画）。</summary>
    public bool EditMode { get; private init; }

    /// <summary>环 1 里的项数（= 父层这一层有几项）。重建时用它判断索引有没有整体移位。</summary>
    public int MenuItemCount => _ring1Items.Count;

    /// <summary>当前显示到哪一级。</summary>
    public LevelPath Path => _path;

    /// <summary>环 1 某项的标签（按顺序）。探针用它核对"环 1 是不是旧的环 2"。</summary>
    public IReadOnlyList<string> Ring1Labels => _ring1Items.Select(i => i.Label).ToList();

    /// <summary>环 1 某项是否有子菜单。点击是否展开/下钻看它。</summary>
    public bool RootItemHasChildren(int index) =>
        index >= 0 && index < _ring1Items.Count && _ring1Items[index].HasChildren;

    /// <summary>环 1 某项的完整标签（未截断）。</summary>
    public string RootItemLabel(int index) =>
        index >= 0 && index < _ring1Items.Count ? _ring1Items[index].Label : "";

    /// <summary>环 1 里某个扇区下各子项的标签。</summary>
    public IReadOnlyList<string> ChildLabelsOf(int parentIndex) =>
        parentIndex >= 0 && parentIndex < _ring1Items.Count
            ? _ring1Items[parentIndex].Children.Select(c => c.Label).ToList()
            : [];

    /// <summary>子弧当前是否可见（含正在展开/收起的中间态）。</summary>
    public bool IsChildArcVisible => _childArcParent >= 0;

    /// <summary>子弧里各项的角度跨度（层级位移要从这些槽位出发，把整层搬到环 1）。</summary>
    internal SectorSpan[] ChildSpans => _childSpans;

    /// <summary>子弧里第 index 项自己还有子项吗（点击时决定"执行"还是"再进一层"）。</summary>
    public bool ChildIndexHasChildren(int index) =>
        index >= 0 && index < _childItems.Count && _childItems[index].HasChildren;

    /// <summary>当前显示的是哪个父扇区的子弧，-1 表示没有。</summary>
    public int ChildArcParent => _childArcParent;

    /// <summary>子弧里的子项数量。</summary>
    public int ChildItemCount => _childSpans.Length;

    /// <summary>诊断用：子环的编辑态虚线是否真的画在视觉树上。</summary>
    public bool ChildEditOutlineAttached =>
        _childEditOutline is not null && Root.Children.Contains(_childEditOutline);

    /// <summary>诊断用：子弧里被截断的标签数。</summary>
    public int ChildElidedLabelCount => _childLabels.Count(l => l.IsElided);

    /// <summary>被悬停子项当前的鼓出像素数 —— 供验证子环形变确实发生了。</summary>
    public double CurrentChildBulge => _childBulge?.CurrentBulgeOf(_hoveredChild) ?? 0;

    /// <summary>子环的形变动画是否还在进行中。</summary>
    public bool IsChildBulgeAnimating => _childBulge?.IsAnimating ?? false;

    /// <summary>子弧当前的展开进度：0 完全收起，1 完全展开。</summary>
    public double ChildArcProgress => _childArcProgress;

    /// <summary>被悬停的子项索引，-1 表示无。</summary>
    public int HoveredChildIndex => _hoveredChild;

    /// <summary>当前选中项的完整路径标签，用于在中心区域显示（长文本读全的另一条路）。</summary>
    public string CurrentSelectionLabel =>
        _hoveredChild >= 0 && _hoveredChild < _childLabels.Count
            ? _childLabels[_hoveredChild].Text
            : _hover >= 0 && _hover < _labels.Length && _hover < _ring1Items.Count
                ? _ring1Items[_hover].Label
                : "";

    /// <summary>
    /// 刻度盘的装饰层：60 条刻度（每 6°，每 5 条更长更亮）+ 一根指针 + 轮毂。
    ///
    /// 刻度是**静态几何**：一次建好就不再重建（只有换皮肤/换菜单才重建整棵视觉树），
    /// 所以它几乎不占逐帧预算。指针只有一个旋转变换，悬停时改一下 RenderTransform。
    /// 全部 IsHitTestVisible = false —— 装饰不该参与命中测试，指着刻度点下去不该有反应。
    /// </summary>
    private static UIElement AddDialDecor(
        Canvas root, Point center, RadiusTokens radius, DesignTokens tokens, Brush stroke)
    {
        var colors = tokens.Color;
        var g = new Canvas
        {
            IsHitTestVisible = false,
            Width = radius.WindowSize,
            Height = radius.WindowSize,
        };

        var outer = radius.Ring1Outer;
        var tickBrush = TokenBrushes.Brush(colors.Label, colors.HintAlpha);

        for (var k = 0; k < RingForms.DialTickCount; k++)
        {
            var deg = k * 360.0 / RingForms.DialTickCount;
            var major = k % 5 == 0;
            var r1 = (outer * 0.93) + (major ? 0 : 3);
            var r2 = (outer * 1.07) - (major ? 0 : 4);
            var a = SectorGeometry.Polar(center, r1, deg);
            var b = SectorGeometry.Polar(center, r2, deg);

            g.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = tickBrush,
                StrokeThickness = major ? 1.6 : 1.0,
                Opacity = major ? 0.75 : 0.35,
            });
        }

        // 指针：从死区边缘指向外圈的一个细长三角形。
        var needle = new Polygon
        {
            Points =
            [
                SectorGeometry.Polar(center, radius.DeadZone + 6, -3.2),
                SectorGeometry.Polar(center, outer * 0.86, 0),
                SectorGeometry.Polar(center, radius.DeadZone + 6, 3.2),
            ],
            Fill = TokenBrushes.Brush(colors.Label, 0.9),
            IsHitTestVisible = false,
        };
        g.Children.Add(needle);

        // 轮毂：让指针的转动有个"轴"。
        var hub = new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = stroke,
            Stroke = TokenBrushes.Brush(colors.Label),
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(hub, center.X - 7);
        Canvas.SetTop(hub, center.Y - 7);
        g.Children.Add(hub);

        root.Children.Add(g);
        return needle;      // 悬停时转的是它
    }

    /// <summary>
    /// 图标。取不到 shell 图标时画首字占位 —— 一个缺失的图标不该让整项看起来像坏了。
    /// </summary>
    /// <summary>
    /// 图标应当按**设备像素**取的边长（DIP × 该屏缩放）。预缩放要用它。
    /// </summary>
    private static int IconDevicePixels(double iconSize)
    {
        var scale = 1.0;
        try { scale = Diagnostics.MonitorLayout.AtCursor().Scale; } catch { /* 取不到就按 1.0 */ }
        return Math.Max(16, (int)Math.Ceiling(iconSize * scale));
    }

    private static Image AddIcon(
        Canvas root, Point center, double radius, double angleDeg, MenuItemSpec item, double iconSize,
        List<UIElement>? placeholderCollector = null)
    {
        var image = new Image
        {
            Width = iconSize,
            Height = iconSize,
        };

        // **取预缩放好的图标，而不是让渲染端去缩。**
        //
        // 这里原来是 HighQuality（Fant 多级滤波）：每个 Image 元素都会建一份多级缓存，
        // 而元素在视觉树重建时被丢弃后，那些非托管缓存不立即归还 ——
        // 实测每次重建净留约 0.66MB（20 次重建 +13.2MB 私有提交字节）。预缩放后位图与显示
        // 尺寸 1:1，缩放缓存不再产生；缩放模式退到廉价的 Linear（1:1 时等于不处理）。
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);

        var source = ShellIcons.GetScaled(item.IconPath, IconDevicePixels(iconSize));
        if (source is not null) image.Source = source;
        else AddPlaceholder(root, center, radius, angleDeg, item.Label, iconSize, placeholderCollector);

        var offset = Vec2.FromPolar(radius, angleDeg);
        Canvas.SetLeft(image, center.X + offset.X - iconSize / 2);
        Canvas.SetTop(image, center.Y + offset.Y - iconSize / 2);
        root.Children.Add(image);
        return image;
    }

    /// <summary>
    /// 画一个加号。用 DrawingImage 而不是画两条线：这样它和其它图标一样是个
    /// <see cref="Image"/>，悬停时的透明度变化、子弧里的渐显都不必为它写特例。
    ///
    /// 十字刻意**不跟随扇区角度旋转** —— 加号转成斜的就认不出来了。
    /// </summary>
    private static Image AddCrossIcon(
        Canvas root, Point center, double radius, double angleDeg, double iconSize, Brush brush)
    {
        var image = new Image
        {
            Width = iconSize,
            Height = iconSize,
            Source = BuildCrossImage(iconSize * 0.62, brush),
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var offset = Vec2.FromPolar(radius, angleDeg);
        Canvas.SetLeft(image, center.X + offset.X - iconSize / 2);
        Canvas.SetTop(image, center.Y + offset.Y - iconSize / 2);
        root.Children.Add(image);
        return image;
    }

    private static DrawingImage BuildCrossImage(double size, Brush brush)
    {
        var half = size / 2;
        var pen = new Pen(brush, Math.Max(1.8, size * 0.17))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            null, pen, new LineGeometry(new Point(-half, 0), new Point(half, 0))));
        group.Children.Add(new GeometryDrawing(
            null, pen, new LineGeometry(new Point(0, -half), new Point(0, half))));
        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <param name="collector">
    /// 非 null 时把创建出来的图形收进去，供调用方在之后一并移除。
    /// 子弧必须传它 —— 否则占位图会永久留在视觉树上（每次展开收起都留一份）。
    /// </param>
    private static void AddPlaceholder(
        Canvas root, Point center, double radius, double angleDeg, string label, double iconSize,
        List<UIElement>? collector = null)
    {
        var tokens = TokenStore.Current;
        var half = iconSize / 2;

        var circle = new Ellipse
        {
            Width = iconSize,
            Height = iconSize,
            Fill = TokenBrushes.Brush(tokens.Color.BadgeFill, tokens.Color.BadgeFillAlpha),
            Stroke = CreateSectorStroke(tokens.Color, tokens.Radius.WindowSize),
            StrokeThickness = 1,
        };

        var offset = Vec2.FromPolar(radius, angleDeg);
        Canvas.SetLeft(circle, center.X + offset.X - half);
        Canvas.SetTop(circle, center.Y + offset.Y - half);
        root.Children.Add(circle);
        collector?.Add(circle);

        var initial = string.IsNullOrEmpty(label) ? "?" : label[..1];
        var text = AddText(root, center, radius, angleDeg, initial,
            TokenBrushes.Brush(tokens.Color.Label, 0.9), iconSize * 0.5);
        collector?.Add(text);
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

    private static Ellipse AddEllipse(
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
        return ellipse;
    }

    /// <summary>
    /// 中心区的两半与提示文字：**左半「Esc」一行、右半「上一层」一行**，中间一条竖中缝。
    /// 右半在第一层时画灰（那时没有"上一层"可回）。
    ///
    /// 编辑态不画两半，只在中心放「完成」一个词 —— 点中心 = 退出编辑态（与旧行为一致，
    /// 编辑时中心不承担导航）。
    ///
    /// **间距的教训**：死区半径是 56（整圆 112 宽），第一版把标签放在 ±15 ——
    /// 只用了可用宽度的一半多一点，两行字几乎贴着中缝（用户反馈"靠得太近"）。
    /// 现在标签中心放在 ±0.47×半径，各自靠向自己那一半的圆心。
    /// </summary>
    private static (TextBlock Hint, TextBlock? Back, Line? Divider, List<TextBlock> ZoneLabels)
        AddCenterParts(Canvas root, Point center, RadiusTokens radius, DesignTokens tokens,
            int depth, bool editMode)
    {
        var fontSize = tokens.Typography.HintFontSize;

        if (editMode)
        {
            var editHint = AddText(root, center, 0, 0, "完成",
                TokenBrushes.Brush(tokens.Color.Hint, tokens.Color.HintAlpha), fontSize);
            return (editHint, null, null, []);
        }

        var available = TokenBrushes.Brush(tokens.Color.Hint, tokens.Color.HintAlpha);
        var backAvailable = CenterZones.IsBackAvailable(depth);
        var backBrush = TokenBrushes.Brush(tokens.Color.Hint,
            backAvailable ? tokens.Color.HintAlpha : tokens.Color.HintAlpha * DimmedHintFactor);

        // 中缝：一条细线把两半分开（装饰，不参与命中 —— 两半的判定在 CenterZones，按角度算）。
        var divider = new Line
        {
            X1 = center.X,
            Y1 = center.Y - radius.DeadZone * 0.55,
            X2 = center.X,
            Y2 = center.Y + radius.DeadZone * 0.55,
            Stroke = TokenBrushes.Brush(tokens.Color.DeadZoneStroke, tokens.Color.DeadZoneStrokeAlpha),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        };
        root.Children.Add(divider);

        // 左半 Esc / 右半 上一层：各一行，中心放在 ±0.47×死区半径 —— 各自那半的中间，
        // 两个标签之间隔着整条中缝加两侧留白。
        var esc = CenterText(root, center.X - radius.DeadZone * 0.47, center.Y, "Esc",
            available, fontSize)[0];
        var back = CenterText(root, center.X + radius.DeadZone * 0.47, center.Y, "上一层",
            backBrush, fontSize * 0.85)[0];

        return (esc, back, divider, [esc, back]);
    }

    /// <summary>在指定位置放一行字（居中）。文字不参与命中测试 —— 点它等于点中心。</summary>
    private static List<TextBlock> CenterText(
        Canvas root, double x, double y, string line, Brush brush, double fontSize)
    {
        var text = new TextBlock
        {
            Text = line,
            FontSize = fontSize,
            Foreground = brush,
            IsHitTestVisible = false,
            TextAlignment = TextAlignment.Center,
        };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Canvas.SetLeft(text, x - text.DesiredSize.Width / 2);
        Canvas.SetTop(text, y - text.DesiredSize.Height / 2);
        root.Children.Add(text);
        return [text];
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

/// <summary>就地改属性的小工具（只为了能在 <c>?.</c> 之后继续赋值）。</summary>
internal static class LetExtensions
{
    public static void Let<T>(this T? value, Action<T> action) where T : class
    {
        if (value is not null) action(value);
    }
}
