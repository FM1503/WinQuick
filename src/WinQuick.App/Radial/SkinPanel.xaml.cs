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
    private void BuildLists()
    {
        _loading = true;
        try
        {
            SkinList.Children.Clear();
            foreach (var skin in _catalog.All)
                SkinList.Children.Add(BuildSkinRow(skin));

            FormList.Children.Clear();
            foreach (var form in new[] { RingForm.Pie, RingForm.Rounded, RingForm.Petal, RingForm.Dial })
                FormList.Children.Add(BuildFormRow(form));
        }
        finally
        {
            _loading = false;
        }
    }

    private UIElement BuildSkinRow(SkinSpec skin)
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
        swatch.Children.Add(SwatchBox(colors.SectorFill, colors.SectorFillAlpha, 22));
        swatch.Children.Add(SwatchBox(colors.HoverFill, colors.HoverFillAlpha, 22, left: 3));
        if (colors.HaloPx > 0)
            swatch.Children.Add(SwatchBox(colors.HaloColor, Math.Max(0.35, colors.HaloAlpha), 22, left: 3));
        Grid.SetColumn(swatch, 0);
        grid.Children.Add(swatch);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = skin.Name,
            Foreground = TokenResources.Brush("Color.Text"),
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.BodySize"),
            FontWeight = FontWeights.SemiBold,
        });
        text.Children.Add(new TextBlock
        {
            Text = skin.Tagline,
            Foreground = TokenResources.Brush("Color.TextMuted"),
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.SmallSize"),
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = "动效：" + MotionSummary(skin),
            Foreground = TokenResources.Brush("Color.TextMuted"),
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
        return row;
    }

    private UIElement BuildFormRow(RingForm form)
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
                        Foreground = TokenResources.Brush("Color.Text"),
                        FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
                        FontSize = TokenResources.Get<double>("Font.BodySize"),
                        FontWeight = FontWeights.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = RingForms.TaglineOf(form),
                        Foreground = TokenResources.Brush("Color.TextMuted"),
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
        BorderBrush = TokenBrushes.Brush("#FFFFFF", 0.18),
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

        // 选中态与色板重画：皮肤一换，面板自己的颜色也变了（资源重装过），
        // 所以这里必须整表重建，不能只改高亮那一行。
        BuildLists();

        FooterStatus.Text = $"已切换到「{_catalog.Find(skinId)?.Name ?? SkinCatalog.DefaultId}」" +
                            $"·形态「{RingForms.NameOf(form)}」，立刻生效并已自动保存。";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
