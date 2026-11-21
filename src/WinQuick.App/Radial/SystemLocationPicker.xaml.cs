using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.Core.Config;

namespace WinQuick.App.Radial;

/// <summary>
/// 「添加到菜单」的选择界面：列出**系统位置**（回收站、此电脑、控制面板…），
/// 以及"浏览文件…"这条老路。
///
/// ## 为什么需要它（用户要求）
///
/// 原来的「＋」直接弹文件选择框，只收 .exe/.lnk/.bat/.cmd —— 而回收站、此电脑、控制面板
/// 这类**虚拟文件夹没有文件系统路径**，根本选不中。用户问"回收站怎么加"，答案一度是
/// "手写 menus.json"，那不该是最终答案。
///
/// ## 三个实现上的讲究
///
/// 1. **候选逐个验证、解析不了的不显示**：`shell:` 别名在不同 Windows 版本上能用的不一样
///    （实测本机 <c>shell:MyPictures</c> / <c>MyMusic</c> / <c>MyVideos</c> / <c>UserProfile</c>
///    都报 DirectoryNotFoundException）。所以取不到图标的项直接不列出来 ——
///    界面里出现的每一项都是这台机器上真的能打开的。
/// 2. **已知文件夹用真实路径**（图片/文档/下载…走 <c>Environment.GetFolderPath</c>）：
///    那样它既是图标来源也是打开目标，不必和别名斗。
/// 3. **它不常驻**：每次要添加时新建、关掉即销毁 —— 不像设置/皮肤面板那样长期持有。
///    所以这里不做"位置记忆"，也刻意不参与常驻内存。
/// </summary>
public partial class SystemLocationPicker : Window
{
    private readonly Action<string, MenuAction> _onChosen;
    private readonly Action? _modalOpening;
    private readonly Action? _modalClosed;

    /// <summary>
    /// 队列语境：文件框放「所有文件」第一档，且非程序类文件按 <c>open</c> 入队。
    /// 与菜单语境（启动器）刻意分开 —— 菜单保持程序类过滤，队列来者不拒。
    /// </summary>
    private readonly bool _queueMode;

    /// <summary>队列语境的来源清单（菜单里有动作的项）；非队列语境为 null。</summary>
    private readonly IReadOnlyList<MenuCatalogEntry>? _menuEntries;

    private bool _closed;

    /// <summary>用户是否手动拖过（与两块面板同一套规则：拖过就不再自动摆位）。</summary>
    public bool PlacedByUser { get; private set; }

    /// <summary>诊断用：列表里实际列出的项数（探针核对"解析不了的不显示"这条规矩）。</summary>
    internal int LocationCountForProbe => LocationList.Children.Count;

    /// <summary>用离屏方式构造（探针核对列表时用，不显示窗口）。</summary>
    internal SystemLocationPicker()
        : this((_, _) => { })
    {
    }

    internal SystemLocationPicker(
        Action<string, MenuAction> onChosen,
        Action? modalOpening = null,
        Action? modalClosed = null,
        bool queueMode = false,
        IReadOnlyList<MenuCatalogEntry>? menuEntries = null)
    {
        InitializeComponent();

        _onChosen = onChosen;
        _modalOpening = modalOpening;
        _modalClosed = modalClosed;
        _queueMode = queueMode;
        _menuEntries = menuEntries;

        SurfaceOps.Watch(this, SurfaceOwner.Panel);
        WindowDrag.Attach(this, TitleBar, onMoved: () => PlacedByUser = true);

        BuildList();

        // Esc / 点外部关闭：与其它面板一致（Esc 是第一反应）。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        Closed += (_, _) =>
        {
            if (_closed) return;
            _closed = true;
            _modalClosed?.Invoke();
        };
    }

    public void PrepareEntrance() => PanelEntrance.Prepare(this);

    public void PlayEntrance() => PanelEntrance.Play(this);

    /// <summary>把候选清单画出来（能取到图标的才画）。</summary>
    private void BuildList()
    {
        var iconPx = IconPixels();
        BuildMenuSection(iconPx);

        foreach (var location in Candidates())
        {
            var icon = ShellIcons.GetScaled(location.Target, iconPx);
            if (icon is null) continue;         // 本机解析不了 → 不显示（见类型注释）

            LocationList.Children.Add(BuildRow(location.Label, icon,
                () => Choose(location.Label, location.ToAction())));
        }
    }

    /// <summary>
    /// 队列语境的「轮盘菜单」分组：把菜单里已有的项列在最前面，加队列项时不必重找路径。
    ///
    /// **图标取不到时画首字圆，而不是像系统位置那样跳过**：系统位置那套「解析不了就不显示」
    /// 是为了不摆出打不开的位置；这里是用户菜单里真实存在、在轮盘上也点得到的项，
    /// 因为一次图标解析失败就从清单里消失，等于让用户以为菜单里没有它。
    /// </summary>
    private void BuildMenuSection(int iconPx)
    {
        if (_menuEntries is not { Count: > 0 }) return;

        LocationList.Children.Add(BuildSectionHeader("轮盘菜单"));
        foreach (var entry in _menuEntries)
        {
            var icon = ShellIcons.GetScaled(entry.Item.IconPath ?? entry.Item.Action?.Target, iconPx);
            // 动作原样传递：类型/参数/工作目录与在轮盘上点它完全一致。
            LocationList.Children.Add(BuildRow(
                entry.Item.Label,
                entry.DisplayLabel,
                icon,
                () => Choose(entry.Item.Label, entry.Item.Action!)));
        }
    }

    /// <summary>分组标题行：加粗小字、不可点 —— 与可点行的外观刻意拉开，避免被误当成候选。</summary>
    private static Border BuildSectionHeader(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TokenResources.Brush("Color.TextMuted"),
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.SmallSize"),
            FontWeight = FontWeights.SemiBold,
        };

        return new Border
        {
            Padding = new Thickness(6, 6, 6, 2),
            Margin = new Thickness(0, 0, 0, 2),
            Child = label,
        };
    }

    /// <summary>
    /// 候选：虚拟文件夹（Core 里的清单）+ 已知文件夹（真实路径）+ 各驱动器。
    /// 顺序按"常用在前"，与 Core 清单一致。
    /// </summary>
    private static IEnumerable<SystemLocation> Candidates()
    {
        foreach (var folder in SystemLocations.VirtualFolders)
            yield return folder;

        foreach (var folder in KnownFolders())
            yield return folder;

        foreach (var drive in Drives())
            yield return drive;
    }

    /// <summary>
    /// 已知文件夹（真实路径）。**刻意用真实路径而不是 shell 别名**：
    /// 别名在本机有几个根本解析不了（见类型注释），而真实路径既是图标来源也是打开目标。
    /// 「桌面」不在这里 —— 它已经由虚拟文件夹那条 <c>shell:Desktop</c> 覆盖，避免重复。
    /// </summary>
    private static IEnumerable<SystemLocation> KnownFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pairs = new (string Label, string Path)[]
        {
            ("用户目录", profile),
            ("下载", string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(profile, "Downloads")),
            ("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };

        foreach (var (label, path) in pairs)
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                yield return new SystemLocation(label, path);
    }

    /// <summary>各驱动器。标签优先用卷标，取不到就用"本地磁盘 (X:)"这类系统描述。</summary>
    private static IEnumerable<SystemLocation> Drives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            // 只列"就绪"的固定盘与可移动盘：光驱空着、网络盘掉线时打开会报错。
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            if (!drive.IsReady) continue;

            var label = drive.VolumeLabel;
            if (string.IsNullOrWhiteSpace(label)) label = drive.DriveType == DriveType.Removable ? "可移动磁盘" : "本地磁盘";
            yield return new SystemLocation($"{label} ({drive.Name.TrimEnd('\\')})", drive.RootDirectory.FullName);
        }
    }

    /// <summary>列表图标按设备像素取（与轮盘上的图标同一条预缩放路径）。</summary>
    private static int IconPixels()
    {
        var scale = 1.0;
        try { scale = MonitorLayout.AtCursor().Scale; } catch { /* 取不到就按 1.0 */ }
        return Math.Max(16, (int)Math.Ceiling(20 * scale));
    }

    private UIElement BuildRow(string label, ImageSource icon, Action onClick) =>
        BuildRow(label, subtitle: null, icon, onClick);

    /// <param name="subtitle">副行文字（队列语境放层级路径）；为 null 时不占行高。</param>
    private UIElement BuildRow(string label, string? subtitle, ImageSource? icon, Action onClick)
    {
        var row = new Border
        {
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 2),
            Background = Brushes.Transparent,
            CornerRadius = TokenResources.Get<CornerRadius>("Size.ControlCornerRadius"),
            Cursor = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var image = new Image
        {
            Source = icon,
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        Grid.SetColumn(image, 0);
        if (icon is not null) grid.Children.Add(image);
        else grid.Children.Add(BuildPlaceholder(label, 20));

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = TokenResources.Brush("Color.Text"),
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.BodySize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (subtitle is not null)
        {
            texts.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = TokenResources.Brush("Color.TextMuted"),
                FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
                FontSize = TokenResources.Get<double>("Font.SmallSize"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        row.Child = grid;
        row.MouseEnter += (_, _) => row.Background = TokenResources.Brush("Color.Stroke");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => onClick();
        return row;
    }

    /// <summary>图标占位圆：与设置面板/轮盘同一规则（徽标底色 + 淡描边 + 名称首字）。</summary>
    private static UIElement BuildPlaceholder(string label, double size)
    {
        var host = new Grid
        {
            Width = size,
            Height = size,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Fill = TokenResources.Brush("Color.BadgeFill"),
            Stroke = TokenResources.Brush("Color.Stroke"),
            StrokeThickness = 1,
        });
        host.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(label) ? "?" : label[..1],
            Foreground = TokenResources.Brush("Color.Text"),
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return host;
    }

    private void Choose(string label, MenuAction action)
    {
        // 注意：**这里不碰 `_modalOpening`** —— 那一对钩子是"本窗口显示/关闭"的计数器，
        // 由调用方在 Show 前 / Closed 里各调一次。选完东西再调一次会把深度加错，
        // 后果是关掉覆盖层之后仍以为自己在一个对话框里（再也关不掉）。
        _onChosen(label, action);
        Close();
    }

    /// <summary>
    /// 「浏览文件…」：还是那条老路（文件选择框），只是现在它是选项之一。
    ///
    /// 队列语境下按扩展名分流：程序类仍走 launch；**其它文件走 open** ——
    /// 文档交给系统关联打开。这与「浏览文件夹」选 open 是同一条语义
    /// （文件夹与文件都是「shell 打开一个路径」），而 launch 的工作目录兜底
    /// 对文档没有意义（文档不需要「从哪个目录启动」这个上下文）。
    /// </summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var path = ProgramPicker.Pick(this, _modalOpening, () => { }, allFilesFirst: _queueMode);
        if (string.IsNullOrWhiteSpace(path)) return;

        Choose(ShellNames.Get(path), ActionForPickedFile(path));
    }

    /// <summary>队列语境的扩展名分流；非队列语境保持原来的一律 launch。</summary>
    private MenuAction ActionForPickedFile(string path)
    {
        if (!_queueMode) return MenuAction.Launch(path);

        var ext = Path.GetExtension(path);
        var isProgram =
            string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase);

        return isProgram
            ? MenuAction.Launch(path)
            : new MenuAction { Type = "open", Target = path };
    }

    /// <summary>
    /// 「浏览文件夹…」：任意目录（不限已知位置）。
    ///
    /// 为什么需要它：下面的列表只覆盖**已知位置**（回收站、下载、各驱动器…），
    /// 而队列编辑器与菜单都可能要加一个随便哪个深层目录 —— 列表再长也覆盖不完。
    ///
    /// 动作照 ActionExecutor 现有语义走 <c>open</c>：它本来就是「用 shell 打开一个路径」，
    /// 目录与文件走同一条 —— 为文件夹另立动作类型就得在 executor 里再分流一次，
    /// 两条语义迟早会不一致。
    /// </summary>
    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        // 以选择器自身为属主：无属主的弹出框不保证压在自己上面，也认不出归属。
        var path = FolderPicker.PickFolder((nint)new WindowInteropHelper(this).Handle);
        if (string.IsNullOrWhiteSpace(path)) return;

        // 根目录（C:\）没有"最后一段"，Path.GetFileName 会返回空串；那种情况直接用完整路径当标签。
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = path;

        Choose(name, new MenuAction { Type = "open", Target = path });
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    internal nint HandleValue => new WindowInteropHelper(this).Handle;
}
