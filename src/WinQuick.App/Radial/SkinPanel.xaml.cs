using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.Core.Design;

namespace WinQuick.App.Radial;

/// <summary>
/// 皮肤选择面板 —— 一个**专门的**换肤界面，与设置面板并列开在覆盖层会话里。
///
/// ## 为什么是独立窗口而不是设置面板里的一节
///
/// 皮肤是要"一个个点过去看"的：轮盘就在旁边，每点一次都要立刻看到效果。
/// 把它塞进设置面板的长表单里，用户要滚上滚下才能比较；单独一个面板可以摆在
/// 轮盘另一侧，一直看着盘面点选。这也是用户提出的形式（"一个专门的皮肤选择界面"）。
///
/// ## 与设置面板的关系：完全对齐
///
/// · 窗口属性逐条一致（无边框、逐像素 alpha、置顶、不进任务栏、SizeToContent=Height）；
/// · 拖动用**同一份** <see cref="WindowDrag"/>（物理像素，拖过就不再自动摆位）；
/// · 入场用**同一份** <see cref="PanelEntrance"/>（含"先清残留动画再写起始值"那条讲究）；
/// · 显示层级由 OverlayController 的 z 序栈声明，紧挨着设置面板那一层；
/// · 配色与字号全部走 DynamicResource —— 所以**它自己也跟着皮肤换色**。
/// </summary>
public partial class SkinPanel : Window
{
    private readonly SkinCatalog _catalog;
    private readonly Action<string, RingForm> _apply;

    private string _currentSkin;
    private RingForm _currentForm;
    private bool _loading;

    public SkinPanel(SkinCatalog catalog, string currentSkinId, RingForm currentForm,
        Action<string, RingForm> apply)
    {
        InitializeComponent();

        _catalog = catalog;
        _currentSkin = currentSkinId;
        _currentForm = currentForm;
        _apply = apply;

        SurfaceOps.Watch(this, SurfaceOwner.Panel);
        WindowDrag.Attach(this, TitleBar, onMoved: () => PlacedByUser = true);

        BuildLists();
    }

    /// <summary>用户是否手动拖过面板。拖过之后覆盖层不再自动摆位（与设置面板同一套规则）。</summary>
    public bool PlacedByUser { get; private set; }

    /// <summary>用户要求关闭（点 ✕）。</summary>
    public event EventHandler? CloseRequested;

    internal nint HandleValue => new WindowInteropHelper(this).Handle;

    /// <summary>诊断用：当前选中的皮肤 id / 形态。</summary>
    internal (string Skin, RingForm Form) SelectionForProbe => (_currentSkin, _currentForm);

    /// <summary>诊断用：列表里渲染出来的皮肤数与形态数。</summary>
    internal (int Skins, int Forms) ItemCountsForProbe => (SkinList.Children.Count, FormList.Children.Count);

    public void PrepareEntrance() => PanelEntrance.Prepare(this);

    public void PlayEntrance() => PanelEntrance.Play(this);

    /// <summary>把列表建出来。皮肤/形态的选中态靠重绘（列表很短，不值得做增量更新）。</summary>
    /// <summary>行控件引用：就地更新选中态用（**不重建列表**，理由见 Select）。</summary>
    private readonly List<(string Id, Border Row, Border?[] Swatches)> _skinRows = [];
    private readonly List<(RingForm Form, Border Row)> _formRows = [];

    private void BuildLists()
    {
        _loading = true;
        try
        {
            _skinRows.Clear();
            _formRows.Clear();
            SkinList.Children.Clear();
            // 第八项「跟随系统」：静态目录之外的运行时合成皮肤，只挂名（spec §5）。
            var items = _catalog.All.Append(SystemSkin.Spec()).ToList();
            foreach (var skin in items)
            {
                var (row, swatches) = BuildSkinRow(skin);
                SkinList.Children.Add(row);
                _skinRows.Add((skin.Id, row, swatches));
            }

            FormList.Children.Clear();
            foreach (var form in new[] { RingForm.Pie, RingForm.Rounded, RingForm.Petal, RingForm.Dial })
            {
                var row = BuildFormRow(form);
                FormList.Children.Add(row);
                _formRows.Add((form, row));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private (Border Row, Border?[] Swatches) BuildSkinRow(SkinSpec skin)
    {
        var isCurrent = string.Equals(skin.Id, _currentSkin, StringComparison.OrdinalIgnoreCase);
        var colors = TokenStore.PreviewTokens(skin.Id).Color;

        var row = new Border
        {
            Background = isCurrent ? TokenResources.Brush("Color.Accent") : Brushes.Transparent,
            BorderBrush = TokenResources.Brush("Color.Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = TokenResources.Get<CornerRadius>("Size.ControlCornerRadius"),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 色板：用这套皮肤**合并后**的真实颜色画两个小方块（扇区底 + 悬停色），
        // 而不是我在这里另写一份预览色 —— 后者迟早和真效果不一致。
        var swatch = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var swatchBoxes = new List<Border?>
        {
            SwatchBox(colors.SectorFill, colors.SectorFillAlpha, 22),
            SwatchBox(colors.HoverFill, colors.HoverFillAlpha, 22, left: 3),
        };
        if (colors.HaloPx > 0)
            swatchBoxes.Add(SwatchBox(colors.HaloColor, Math.Max(0.35, colors.HaloAlpha), 22, left: 3));
        foreach (var box in swatchBoxes) swatch.Children.Add(box!);
        Grid.SetColumn(swatch, 0);
        grid.Children.Add(swatch);


        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // 选中行底是 Accent → 行内文字换成 on-accent 配对色（spec R1；缺陷：半透明白底白字整行消失）。
        var nameBrush = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.Text");
        var noteBrush = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.TextMuted");
        text.Children.Add(new TextBlock
        {
            Text = skin.Name,

            Foreground = nameBrush,
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.BodySize"),
            FontWeight = FontWeights.SemiBold,
        });
        text.Children.Add(new TextBlock
        {
            Text = skin.Tagline,

            Foreground = noteBrush,
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.SmallSize"),
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = "动效：" + MotionSummary(skin),

            Foreground = noteBrush,
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.SmallSize"),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        row.Child = grid;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (_loading) return;
            Select(skin.Id, _currentForm);
        };
        return (row, swatchBoxes.ToArray());
    }

    private Border BuildFormRow(RingForm form)
    {
        var isCurrent = form == _currentForm;

        var row = new Border
        {
            Background = isCurrent ? TokenResources.Brush("Color.Accent") : Brushes.Transparent,
            BorderBrush = TokenResources.Brush("Color.Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = TokenResources.Get<CornerRadius>("Size.ControlCornerRadius"),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = RingForms.NameOf(form),

                        Foreground = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.Text"),
                        FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
                        FontSize = TokenResources.Get<double>("Font.BodySize"),
                        FontWeight = FontWeights.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = RingForms.TaglineOf(form),

                        Foreground = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.TextMuted"),
                        FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
                        FontSize = TokenResources.Get<double>("Font.SmallSize"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        row.MouseLeftButtonUp += (_, _) =>
        {
            if (_loading) return;
            Select(_currentSkin, form);
        };
        return row;
    }

    private static Border SwatchBox(string hex, double alpha, double size, double left = 0) => new()
    {
        Width = size,
        Height = size,
        Margin = new Thickness(left, 0, 0, 0),
        CornerRadius = new CornerRadius(4),
        Background = TokenBrushes.Brush(hex, alpha),
        BorderBrush = TokenResources.Brush("Color.Outline"),
        BorderThickness = new Thickness(1),
    };

    /// <summary>这套皮肤的动效摘要（时长与曲线），直接来自合并后的令牌。</summary>
    private static string MotionSummary(SkinSpec skin)
    {
        var tokens = TokenStore.PreviewTokens(skin.Id);
        var m = tokens.Motion;
        var rotate = Math.Abs(m.EntranceFromRotateDeg) > 0.01
            ? $"旋转 {m.EntranceRotateMs}ms"
            : "不旋转";
        var steps = MotionTokens.TryParseSteps(m.EntranceScaleEasing, out var n) ? $"（{n} 级阶梯）" : "";
        return $"缩放 {m.EntranceScaleMs}ms{steps} / {rotate} / 子环 {m.ChildSlideMs}ms" +
               (m.ChildStaggerMs > 0 ? $"（错开 {m.ChildStaggerMs}ms）" : "");
    }

    private void Select(string skinId, RingForm form)
    {
        _currentSkin = skinId;
        _currentForm = form;

        _apply(skinId, form);

        // **就地更新选中态，不重建列表。**
        //
        // 原来这里是 BuildLists()（清空重建 6+4 行）。实测的代价：这是**分层窗口里被渲染过**的内容，
        // 每重建一次就净留一份渲染数据（在轮盘上量到约 0.65MB/次，强制回收也只回来一半）——
        // 而点皮肤是这套界面里最频繁的操作，点十几下就能把常驻内存顶上去。
        // 选中态本来只改若干 Border 的画刷，没有任何新元素需要创建。
        UpdateSelectionVisuals();

        FooterStatus.Text = $"已切换到「{(skinId == SystemSkin.Id ? SystemSkin.Spec().Name : _catalog.Find(skinId)?.Name ?? SkinCatalog.DefaultId)}」" +
                            $"·形态「{RingForms.NameOf(form)}」，立刻生效并已自动保存。";
    }

    /// <summary>
    /// 就地把"当前选中"的画刷刷一遍（皮肤与形态两组行）。
    ///
    /// 皮肤一换，面板自己的配色也跟着变了（资源已重装），所以这里把所有行的
    /// 背景/描边/色板都按新规格重刷，而不是重建行。
    /// </summary>
    private void UpdateSelectionVisuals()
    {
        var stroke = TokenResources.Brush("Color.Stroke");
        var accent = TokenResources.Brush("Color.Accent");
        var transparent = Brushes.Transparent;

        foreach (var (id, row, swatches) in _skinRows)
        {
            var isCurrent = string.Equals(id, _currentSkin, StringComparison.OrdinalIgnoreCase);
            row.Background = isCurrent ? accent : transparent;
            row.BorderBrush = stroke;

            // 色板也重刷：这是换皮肤后唯一必须跟着变的东西。
            var colors = TokenStore.PreviewTokens(id).Color;
            if (swatches.Length > 0) swatches[0]!.Background = TokenBrushes.Brush(colors.SectorFill, colors.SectorFillAlpha);
            if (swatches.Length > 1) swatches[1]!.Background = TokenBrushes.Brush(colors.HoverFill, colors.HoverFillAlpha);
            if (swatches.Length > 2) swatches[2]!.Background = TokenBrushes.Brush(colors.HaloColor, Math.Max(0.35, colors.HaloAlpha));
        }

        foreach (var (form, row) in _formRows)
        {
            row.Background = form == _currentForm ? accent : transparent;
            row.BorderBrush = stroke;
        }

        // 面板里所有文字的颜色也跟着新规格走（资源已重装，DynamicResource 会自己更新，
        // 但这里是"程序化创建的控件"，它们的 Foreground 是构造时按老规格设的 —— 要手动重刷）。
        RefreshTextColors();
    }

    /// <summary>把面板里程序化创建的文字颜色重刷成当前规格（DynamicResource 管不到代码建的控件）。</summary>

    private void RefreshTextColors()
    {
        var text = TokenResources.Brush("Color.Text");
        var muted = TokenResources.Brush("Color.TextMuted");
        var onAccent = TokenResources.Brush("Color.AccentText");
        var family = TokenResources.Get<FontFamily>("Font.Family");
        var body = TokenResources.Get<double>("Font.BodySize");
        var small = TokenResources.Get<double>("Font.SmallSize");

        // 选中行底是 Accent → 文字必须换配对色 AccentText（spec R1）。
        // 旧实现按字重统一刷 Text/TextMuted，半透明皮肤就白底白字 —— 用户截图里那行「整块纯白无文字」。
        void RefreshRow(Border row, bool isCurrent)
        {
            foreach (var block in Descendants(row).OfType<TextBlock>())
            {
                block.FontFamily = family;
                var isBody = block.FontWeight == FontWeights.SemiBold;
                block.FontSize = isBody ? body : small;
                block.Foreground = isCurrent ? onAccent : (isBody ? text : muted);
            }
        }

        foreach (var (id, row, _) in _skinRows)
            RefreshRow(row, string.Equals(id, _currentSkin, StringComparison.OrdinalIgnoreCase));
        foreach (var (form, row) in _formRows)
            RefreshRow(row, form == _currentForm);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grand in Descendants(child)) yield return grand;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
