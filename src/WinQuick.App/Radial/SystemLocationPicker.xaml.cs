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
    private bool _closed;

    /// <summary>用户是否手动拖过（与两块面板同一套规则：拖过就不再自动摆位）。</summary>
    public bool PlacedByUser { get; private set; }

    /// <summary>诊断用：列表里实际列出的项数（探针核对"解析不了的不显示"这条规矩）。</summary>
    internal int LocationCountForProbe => LocationList.Children.Count;

    /// <summary>用离屏方式构造（探针核对列表时用，不显示窗口）。</summary>
    internal SystemLocationPicker()
        : this((_, _) => { }, null, null)
    {
    }

    internal SystemLocationPicker(
        Action<string, MenuAction> onChosen,
        Action? modalOpening = null,
        Action? modalClosed = null)
    {
        InitializeComponent();

        _onChosen = onChosen;
        _modalOpening = modalOpening;
        _modalClosed = modalClosed;

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
        foreach (var location in Candidates())
        {
            var icon = ShellIcons.GetScaled(location.Target, iconPx);
            if (icon is null) continue;         // 本机解析不了 → 不显示（见类型注释）

            LocationList.Children.Add(BuildRow(location.Label, icon,
                () => Choose(location.Label, location.ToAction())));
        }
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

    private UIElement BuildRow(string label, ImageSource icon, Action onClick)
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
        grid.Children.Add(image);

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TokenResources.Brush("Color.Text"),
            FontFamily = TokenResources.Get<FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.BodySize"),
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        row.Child = grid;
        row.MouseEnter += (_, _) => row.Background = TokenResources.Brush("Color.Stroke");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => onClick();
        return row;
    }

    private void Choose(string label, MenuAction action)
    {
        // 注意：**这里不碰 `_modalOpening`** —— 那一对钩子是"本窗口显示/关闭"的计数器，
        // 由调用方在 Show 前 / Closed 里各调一次。选完东西再调一次会把深度加错，
        // 后果是关掉覆盖层之后仍以为自己在一个对话框里（再也关不掉）。
        _onChosen(label, action);
        Close();
    }

    /// <summary>「浏览文件…」：还是那条老路（文件选择框），只是现在它是选项之一。</summary>
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var path = ProgramPicker.Pick(this, _modalOpening, () => { });
        if (string.IsNullOrWhiteSpace(path)) return;

        Choose(ShellNames.Get(path), MenuAction.Launch(path));
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    internal nint HandleValue => new WindowInteropHelper(this).Handle;
}
