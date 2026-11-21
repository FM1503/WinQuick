using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using WinQuick.App.Config;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.App.Interop;
using WinQuick.App.Settings;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using WinQuick.Core.Hotkey;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Radial;

/// <summary>
/// 设置面板 —— 直接开在覆盖层会话里，而不是另开一个独立对话框。
///
/// 为什么改成这样（用户反馈驱动）：
/// 1. **视觉统一**。原先是独立对话框，样式在 XAML 里硬编码，与轮盘各写一套，
///    很快就长得不一样。现在全部走令牌（<see cref="TokenResources"/>），与轮盘同源。
/// 2. **改动立刻可见**。面板与轮盘同时在场，拖遮罩滑块、改高亮色、调悬停延迟，
///    都能马上在旁边的轮盘上看到效果 —— 独立对话框做不到这一点，
///    因为它占着前台、覆盖层已经关掉了。
///
/// 与操作条的区别：操作条是 WS_EX_NOACTIVATE（绝不能抢焦点），
/// 而面板**需要激活** —— 热键录制与文本框都要键盘输入。
/// 这会让轮盘失去焦点，由 OverlayController 的"焦点是否落在自己人身上"守卫兜住，
/// 不会误关覆盖层。
/// </summary>
public partial class SettingsPanel : Window
{
    private const double DefaultDimOpacity = 0.4;

    private readonly AppConfig _original;
    private readonly Func<HotkeySpec, (bool Available, int ErrorCode)> _probeHotkey;
    private readonly Action<AppConfig> _apply;

    private HotkeySpec? _pendingHotkey;
    private bool _capturing;

    /// <summary>当前选中的高亮色（#RRGGBB）；null = 默认（沿用设计基线）。</summary>
    private string? _accent;

    /// <summary>调色盘「最近使用」（新的在前，最多 6 个）。持久化进配置。</summary>
    private IReadOnlyList<string> _recent = [];

    /// <summary>HSV 视图的当前值（h ∈ [0,360)，s/v ∈ [0,1]）。</summary>
    private double _hsvH = 210, _hsvS = 0.55, _hsvV = 0.95;

    /// <summary>调色盘上一次被关掉的时刻（防“点按钮关盘被当成又要开”，见 WireAccentFlyout）。</summary>
    private long _flyoutClosedAt;

    /// <summary>
    /// 正在按程序化方式改控件（初始化或恢复默认）。
    /// 期间不能触发"改动即应用"，否则打开面板这一个动作本身就会写一次配置。
    /// </summary>
    private bool _loading;

    public SettingsPanel(
        AppConfig current,
        Func<HotkeySpec, (bool Available, int ErrorCode)> probeHotkey,
        Action<AppConfig> applyConfig)
    {
        InitializeComponent();

        _original = current;
        _probeHotkey = probeHotkey;
        _apply = applyConfig;

        // 窗口级操作计数（--flicker-probe）。
        SurfaceOps.Watch(this, SurfaceOwner.Panel);

        // 标题栏按住即可拖动（物理像素实现，见 WindowDrag 里的两条"为什么"）。
        // 拖过之后不再自动摆位 —— 面板挡住轮盘正是拖动的动机。
        WindowDrag.Attach(this, TitleBar, onMoved: () => PlacedByUser = true);

        NavList.SelectedIndex = 0;

        WireAccentFlyout();

        LoadFrom(current, notify: false);

        AboutVersion.Text = $"版本 {Updates.UpdateChecker.CurrentVersion ?? "（开发运行）"}";
    }

    /// <summary>侧栏切换分区：右侧只显示当前区，标题同步。</summary>
    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageHeader is null) return;   // InitializeComponent 期间也会触发一次

        var pages = new UIElement[] { PageHotkey, PageLook, PageBehavior, PageMenu, PageQueues, PageStartup, PageAbout };
        var index = NavList.SelectedIndex;
        for (var i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        PageHeader.Text = NavList.SelectedItem is ListBoxItem item ? item.Content.ToString() ?? "" : "";

        // 启动队列分区**每次变为可见时**重新取数：队列文件也可能被别人改
        // （手改 launch-queues.json、另一个进程写），一直挂着一份旧工作副本
        // 会让下一次"改一下存一次"把别人刚写的内容覆盖掉。
        if (pages[index] == PageQueues) LoadQueuesFromSource();
    }

    /// <summary>热键预设：选中即填入并立即探测注册（成了就应用）；「自定义…」转去录制框。</summary>
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox is null || _loading) return;

        if (PresetBox.SelectedItem is not string choice || choice == CustomPresetItem)
        {
            // 自定义：聚焦录制框，用户直接按组合键（现有流程）。
            HotkeyBox.Focus();
            SetHotkeyStatus("正在等待按键……请按下想要的组合键。", isError: false);
            return;
        }

        if (!HotkeySpec.TryParse(choice, out var spec, out _) || spec is null) return;

        _pendingHotkey = spec;
        HotkeyBox.Text = spec.ToString();
        TryProbeAndApply();   // 预设是明确选择，直接走「测试并应用」
    }

    private const string CustomPresetItem = "自定义…";

    /// <summary>把当前 _pendingHotkey 探测一遍，可用则应用，不可用则把原因写到状态行。</summary>
    private void TryProbeAndApply()
    {
        if (_pendingHotkey is not { } spec) return;

        var (available, errorCode) = _probeHotkey(spec);

        if (available)
        {
            SetHotkeyStatus($"可用。正在切换到 {spec}。", isError: false);
            ApplyAndPersist();
            return;
        }

        var reason = errorCode switch
        {
            1409 => "已被系统或其他程序占用",
            87 => "这个组合不合法",
            _ => $"注册失败（错误码 {errorCode}）",
        };
        var suggestion = HotkeySpec.SuggestIfReserved(spec);

        SetHotkeyStatus(
            $"{spec}：{reason}。" + (suggestion is null
                ? "换一个组合试试。"
                : "Win11 上只带 Win 键的组合大多被系统预留 —— " + suggestion),
            isError: true);
    }

    /// <summary>
    /// 用户是否手动拖过面板。拖过之后就不再自动摆位 ——
    /// 面板挡住轮盘正是拖动的动机，自动摆位会把它拽回原处，等于白拖。
    /// </summary>
    public bool PlacedByUser { get; private set; }

    /// <summary>
    /// 按住标题栏拖动面板。
    ///
    /// 为什么要做：面板固定摆在光标所在屏的另一侧，但那个"另一侧"是估的 ——
    /// 轮盘跟着光标走，总有一些位置会重叠。让用户自己拖开是最省事的解法。
    ///
    /// **最初用的是 `Window.DragMove()`，实测拖不动。** 它要求窗口处于激活状态
    ///（内部会检查 `IsActive`），不满足就抛异常 —— 而我当时把异常静默吞了，
    /// 于是表现成"按下去毫无反应"，连原因都看不到。吞异常比抛异常更坏。
    ///
    /// 现在改成自己实现：记下按下的屏幕坐标与窗口位置，之后按位移挪窗口。
    /// 全部用**物理像素**（`GetCursorPos` / `GetWindowRect` / `SetWindowPos`），
    /// 绕开 DIP 换算 —— 在 175% 这类非整数缩放下按 DIP 算会逐帧累积误差。
    /// </summary>
    /// <summary>探针用：当前是否正在拖动。</summary>
    internal bool IsDraggingForTest => WindowDrag.IsDragging(this);

    /// <summary>探针用：是否已标记为被用户摆放过。</summary>
    internal bool PlacedByUserForTest => PlacedByUser;

    /// <summary>
    /// 把面板左下角的位置报出去，供"用户拖过之后记住位置"使用。
    /// 用物理像素，避免 DIP 换算在非整数缩放下丢精度。
    /// </summary>
    public (int X, int Y)? CurrentPhysicalPosition
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return null;
            return Windows.Win32.PInvoke.GetWindowRect(
                (Windows.Win32.Foundation.HWND)hwnd, out var rect)
                ? (rect.left, rect.top)
                : null;
        }
    }

    /// <summary>用户要求关闭设置（点 ✕ 或按 Esc）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>用户要求打开皮肤选择面板（外观区里那个按钮）。</summary>
    public event EventHandler? SkinRequested;

    private void OnSkinClick(object sender, RoutedEventArgs e) =>
        SkinRequested?.Invoke(this, EventArgs.Empty);

    internal nint HandleValue => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 恢复显示前的占位。真正的定位由 OverlayController 负责（它知道光标在哪块屏），
    /// 这里只保证窗口不会先出现在屏幕角落再跳过去。
    /// </summary>
    public void PlaceAtMonitorCornerIfNeeded()
    {
        // 刻意留空：定位交给 OverlayController.PositionSettingsPanel，
        // 它会把面板放在光标所在屏的另一侧并钳制在屏内。
    }

    /// <summary>
    /// 把面板重新插回 topmost band 最前。
    /// 遮罩与轮盘都是置顶的，面板也必须一直置顶 ——
    /// 否则它会被压在遮罩下面，既看不见也点不到（这正是用户报的现象）。
    /// </summary>
    public void ReassertTopmost()
    {
        if (!IsVisible) return;

        var hwnd = (HWND)HandleValue;
        ZOrderManager.BringToTopmostFront(hwnd);
    }

    // ---------------------------------------------------------------- 初始化

    private void LoadFrom(AppConfig config, bool notify)
    {
        _loading = !notify;

        _pendingHotkey = HotkeySpec.TryParse(config.Hotkey, out var spec, out _) && spec is not null
            ? spec
            : HotkeySpec.Default;

        HotkeyBox.Text = _pendingHotkey.ToString();
        SyncPresetSelection();
        SetHotkeyStatus("点「测试」试一下这个组合能不能注册。", isError: false);

        // 与默认值相同时滑块回到默认位置，并在文案里标明"默认" ——
        // 不然用户分不清"我改过没有"。
        DimSlider.Value = config.Appearance.DimOpacity ?? DefaultDimOpacity;
        UpdateDimLabel(config.Appearance.DimOpacity);

        _accent = config.Appearance.AccentColor;
        _recent = config.Appearance.RecentAccentColors;
        SyncAccentVisual();

        if (AnimationsBox.Items.Count == 0)
        {
            AnimationsBox.Items.Add("跟随系统");
            AnimationsBox.Items.Add("始终播放");
            AnimationsBox.Items.Add("始终不播放");
        }
        AnimationsBox.SelectedIndex = config.Behavior.AnimationsEnabled switch
        {
            null => 0,
            true => 1,
            false => 2,
        };

        HideOnFocusLossBox.IsChecked = config.Behavior.HideOnFocusLoss;

        // 自启的事实来源是注册表（托盘菜单也能改），所以读注册表而不是配置。
        AutoStartBox.IsChecked = AutoStart.IsEnabled;

        AutoUpdateBox.IsChecked = config.Updates.AutoCheck;
        ShellMenuBox.IsChecked = config.ShellMenu;

        _loading = false;

        if (notify) ApplyAndPersist();
    }

    private void UpdateDimLabel(double? explicitValue) =>
        DimValue.Text = explicitValue is null
            ? $"{DimSlider.Value * 100:0}% 默认"
            : $"{DimSlider.Value * 100:0}%";

    /// <summary>进入设置时播放与轮盘一致的入场动画，观感上是覆盖层的一部分。</summary>
    /// <summary>入场动画的起始缩放。Prepare 与 Play 必须用同一个值，所以抽成常量。</summary>
    private const double EntranceFromScale = 0.94;

    private System.Windows.Media.ScaleTransform? _entranceScale;

    /// <summary>
    /// 把入场动画的**起始状态**先落定。必须在 <c>Show()</c> **之前**调用 ——
    /// 否则面板会先以终态渲染一两帧、动画才开始，看起来是"先闪一下再缩放"。
    /// 详见 <see cref="RadialWindow.PrepareEntrance"/>。
    /// </summary>
    public void PrepareEntrance()
    {
        if (Content is not FrameworkElement root) return;
        if (!Design.MotionPreference.SystemAllows) return;   // 不播动画就别预先缩小

        // **先把上一轮残留的动画清掉，再写起始值。**
        //
        // PlayEntrance 挂的是关键帧动画，默认 HoldEnd —— 动画结束后
        // "动画值 1"会一直压着本地值。于是第二次打开面板时，
        // 这里写的 Opacity = 0 会被那条残留动画的 1 盖掉：
        // 面板先以**终态**完整显示几帧，然后才被新动画拽回 0 重新长出来。
        // 观感就是"先闪一下整个面板，再缩放" —— 用户报的正是这个。
        //
        // 轮盘那份 PrepareEntrance 一直有这几句（所以轮盘没这个毛病），
        // 面板当初照着抄却漏了，只有第一次打开是对的（那时还没有残留动画）。
        // 判据在 --flash-probe 的"入场状态（应用侧逐帧）"：触发后首帧必须是 0。
        root.BeginAnimation(OpacityProperty, null);

        if (root.RenderTransform is System.Windows.Media.ScaleTransform previous)
        {
            previous.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            previous.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        }

        _entranceScale = new System.Windows.Media.ScaleTransform(EntranceFromScale, EntranceFromScale);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = _entranceScale;
        root.Opacity = 0;
    }

    /// <summary>诊断用：入场起始状态是否已经落定（缩放值, 透明度）。</summary>
    internal (double Scale, double Opacity) EntranceStateForProbe =>
        (Content is FrameworkElement r ? r.RenderTransform is System.Windows.Media.ScaleTransform s ? s.ScaleX : 1.0 : 1.0,
         Content is FrameworkElement e ? e.Opacity : 1.0);

    public void PlayEntrance()
    {
        // 变换必须挂在**根内容元素**上，不能挂在 Window 上：
        // WPF 的 Window 会对 RenderTransform 做强制转换（CoerceRenderTransform）并抛
        // 「转换对于 Window 无效」。轮盘窗口当初也是为此把变换挂在根视觉元素上的，
        // 我写这个面板时忘了这一点 —— 结果面板一打开就崩，
        // 表现正是用户报的"点设置没有显示设置界面"。
        // （这个崩溃只在真正打开面板时发生，所以只有走真实集成路径的探针才抓得到。）
        if (Content is not FrameworkElement root) return;

        var duration = TokenResources.Get<TimeSpan>("Motion.Duration");
        var spline = TokenResources.Get<KeySpline>("Motion.Easing");

        // 复用 PrepareEntrance 建好的那个变换：两处各建一个的话，
        // 显示的起始状态与动画的起点就会是两个不同的对象，改一处忘一处。
        var scale = _entranceScale ?? new System.Windows.Media.ScaleTransform(EntranceFromScale, EntranceFromScale);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;

        var scaleAnimation = new DoubleAnimationUsingKeyFrames();
        scaleAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(EntranceFromScale, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scaleAnimation.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(duration), spline));

        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleAnimation);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleAnimation);

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration), spline));
        root.BeginAnimation(OpacityProperty, fade);
    }

    // ---------------------------------------------------------------- 热键

    private void OnHotkeyFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturing) return;
        _capturing = true;
        SetHotkeyStatus("正在等待按键……请按下想要的组合键。", isError: false);
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // Esc 用来关面板，不能被当成热键录走。
        if (e.Key is Key.Escape)
        {
            _capturing = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Tab 用来在控件间移动焦点。
        if (e.Key is Key.Tab)
        {
            _capturing = false;
            return;
        }

        if (!HotkeyRecorder.TryCapture(e.Key, Keyboard.Modifiers, out var spec, out var error))
        {
            SetHotkeyStatus(error ?? "无法识别这个按键。", isError: true);
            return;
        }

        _capturing = false;
        _pendingHotkey = spec;
        HotkeyBox.Text = spec!.ToString();
        SyncPresetSelection();

        var suggestion = HotkeySpec.SuggestIfReserved(spec);
        SetHotkeyStatus(suggestion ?? "点「测试」试一下能不能注册。", isError: false);

        // 录制后不自动应用 —— 用户可能只是在试。误改一个正在生效的热键会让工具突然"消失"。
    }

    private void OnTestHotkeyClick(object sender, RoutedEventArgs e) => TryProbeAndApply();

    /// <summary>
    /// 让预设下拉反映当前热键：与某个预设相同就选中它，否则选「自定义…」。
    /// （在代码里改 HotkeyBox 的地方都要跟一次，不然下拉显示的与实际不符。）
    /// </summary>
    private void SyncPresetSelection()
    {
        if (PresetBox is null) return;

        if (PresetBox.Items.Count == 0)
        {
            foreach (var preset in HotkeySpec.RecommendedPresets) PresetBox.Items.Add(preset);
            PresetBox.Items.Add(CustomPresetItem);
        }

        var current = _pendingHotkey?.ToString();
        var match = HotkeySpec.RecommendedPresets.FirstOrDefault(p => p == current);
        _loading = true;
        PresetBox.SelectedItem = match ?? CustomPresetItem;
        _loading = false;
    }

    private void SetHotkeyStatus(string text, bool isError)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.Foreground = isError
            ? TokenResources.Brush("Color.Danger")
            : TokenResources.Brush("Color.Success");
    }

    // ---------------------------------------------------------------- 各控件：改动即应用

    private void OnDimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged 在 InitializeComponent 期间也会触发，那时 DimValue 还没赋值。
        if (DimValue is null || _loading) return;

        UpdateDimLabel(null);
        ApplyAndPersist();
    }

    // ---------------------------------------------------------------- 高亮色调色盘

    /// <summary>
    /// 调色盘的交互接线：色格点击、SV/色相拖拽、点外面收起、Esc 先收盘。
    /// </summary>
    private void WireAccentFlyout()
    {
        // 色板 + 最近使用：格子全部在代码里建（XAML 只留容器）。
        foreach (var hex in ColorMath.RecommendedAccentPalette)
            FlyGrid.Children.Add(MakeCell(hex));
        FlyDefaultHost.Content = MakeDefaultCell();
        FlyDefaultHost2.Content = MakeDefaultCell();

        // 点外面收起由 StaysOpen=False 自带（包括拖标题栏 —— 那也是“盘外”）。
        // 但要点在**别的程序**上时它收不到鼠标消息，失活时补一刀。
        Deactivated += (_, _) => CloseAccentFlyout();

        // StaysOpen=False 的经典坑：点色块按钮关盘时，那一下先被当成“盘外点击”把盘关掉，
        // 紧接着按钮的 Click 又会把盘重新打开 —— 开关就失灵了。
        // 记下关闭时刻：按钮点击发生在关闭后的一瞬之内就当作“用户是想关”。
        AccentFlyout.Closed += (_, _) => _flyoutClosedAt = Environment.TickCount64;

        // Esc 先收调色盘，再走原有的关面板路径。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && AccentFlyout.IsOpen)
            {
                e.Handled = true;
                CloseAccentFlyout();
            }
        };

        // SV 平面与色相条：按下即捕获指针，移动实时预览，松手才提交（进最近使用并落盘）。
        SvSquare.MouseLeftButtonDown += (_, e) => { SvSquare.CaptureMouse(); UpdateFromSv(e.GetPosition(SvSquare), preview: true); };
        SvSquare.MouseMove += (_, e) => { if (SvSquare.IsMouseCaptured) UpdateFromSv(e.GetPosition(SvSquare), preview: true); };
        SvSquare.MouseLeftButtonUp += (_, _) => { SvSquare.ReleaseMouseCapture(); CommitHsv(); };

        HueBar.MouseLeftButtonDown += (_, e) => { HueBar.CaptureMouse(); UpdateFromHue(e.GetPosition(HueBar), preview: true); };
        HueBar.MouseMove += (_, e) => { if (HueBar.IsMouseCaptured) UpdateFromHue(e.GetPosition(HueBar), preview: true); };
        HueBar.MouseLeftButtonUp += (_, _) => { HueBar.ReleaseMouseCapture(); CommitHsv(); };

        // HSV 视图里的 hex 框：输完整才生效（与旧文本框同一条护栏）。
        FlyHex.TextChanged += (_, _) =>
        {
            if (_loading) return;
            var text = FlyHex.Text.Trim();
            if (!ColorMath.TryParseHex(text, out var r, out var g, out var b)) return;
            CommitAccent(ColorMath.ToHex(r, g, b));
        };
    }

    private Border MakeCell(string hex)
    {
        var cell = new Border
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = TokenBrushes.Brush(hex),
            BorderBrush = TokenBrushes.Brush("#FFFFFF", 0.15),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = hex,
        };
        cell.MouseLeftButtonDown += (_, _) => CommitAccent(hex);
        return cell;
    }

    /// <summary>「默认」格：两色底 + 一道斜杠（斜杠 = 没有自定义色）。点它清掉自定义。</summary>
    private Border MakeDefaultCell()
    {
        var cell = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = TokenBrushes.Brush("#2A2F3A"),
            BorderBrush = TokenBrushes.Brush("#FFFFFF", 0.15),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "默认（清掉自定义色）",
        };
        var slash = new System.Windows.Shapes.Rectangle
        {
            Width = 26,
            Height = 2,
            Fill = TokenBrushes.Brush("#E08A7A"),
            RenderTransform = new System.Windows.Media.RotateTransform(-32),
        };
        cell.Child = slash;
        cell.MouseLeftButtonDown += (_, _) => CommitAccent(null);
        return cell;
    }

    private void OnAccentButtonClick(object sender, RoutedEventArgs e)
    {
        // 刚被“盘外点击”关掉的一瞬（点的就是这个按钮）—— 这一下的意图是关，不是再开。
        if (Environment.TickCount64 - _flyoutClosedAt < 200) return;

        if (AccentFlyout.IsOpen) CloseAccentFlyout();
        else OpenAccentFlyout();
    }

    private void OpenAccentFlyout()
    {
        FlyHsv.Visibility = Visibility.Collapsed;
        FlyPalette.Visibility = Visibility.Visible;

        // 最近使用行：有才显示。
        var hasRecent = _recent.Count > 0;
        FlyRecentLabel.Visibility = hasRecent ? Visibility.Visible : Visibility.Collapsed;
        FlyRecent.Visibility = hasRecent ? Visibility.Visible : Visibility.Collapsed;
        FlyRecent.Children.Clear();
        foreach (var hex in _recent) FlyRecent.Children.Add(MakeCell(hex));

        AccentFlyout.IsOpen = true;
    }

    private void CloseAccentFlyout() => AccentFlyout.IsOpen = false;

    private void OnFlyMoreClick(object sender, RoutedEventArgs e)
    {
        // 进 HSV 前先把当前颜色换算成 HSV —— 拾色器要「从现在这个颜色」开始，而不是从固定值。
        if (ColorMath.TryParseHex(_accent, out var r, out var g, out var b))
            (_hsvH, _hsvS, _hsvV) = ColorMath.RgbToHsv(r, g, b);

        FlyPalette.Visibility = Visibility.Collapsed;
        FlyHsv.Visibility = Visibility.Visible;
        SyncHsvVisual();
    }

    private void OnFlyBackClick(object sender, RoutedEventArgs e)
    {
        FlyHsv.Visibility = Visibility.Collapsed;
        FlyPalette.Visibility = Visibility.Visible;
    }

    /// <summary>拖拽中的实时预览：改色与视觉，不进最近使用（那是松手的事）。</summary>
    private void UpdateFromSv(Point p, bool preview)
    {
        _ = preview;
        _hsvS = Math.Clamp(p.X / SvSquare.ActualWidth, 0, 1);
        _hsvV = Math.Clamp(1 - p.Y / SvSquare.ActualHeight, 0, 1);
        SyncHsvVisual();
        LiveAccent(HsvHex());
    }

    private void UpdateFromHue(Point p, bool preview)
    {
        _ = preview;
        _hsvH = Math.Clamp(p.X / HueBar.ActualWidth, 0, 1) * 360.0;
        SyncHsvVisual();
        LiveAccent(HsvHex());
    }

    private string HsvHex()
    {
        var (r, g, b) = ColorMath.HsvToRgb(_hsvH, _hsvS, _hsvV);
        return ColorMath.ToHex(r, g, b);
    }

    private void CommitHsv()
    {
        var hex = HsvHex();
        FlyHex.Text = hex;
        CommitAccent(hex);
    }

    /// <summary>实时预览：改当前色并应用（落盘有去抖），但不碰最近使用。</summary>
    private void LiveAccent(string hex)
    {
        _accent = hex;
        SyncAccentVisual();
        if (!_loading) ApplyAndPersist();
    }

    /// <summary>提交一次选择（点色格 / 默认 / 拖完松手 / hex 输完整）：进最近使用并应用。</summary>
    private void CommitAccent(string? hex)
    {
        _accent = hex;
        if (hex is not null) _recent = ColorMath.UpdateRecent(_recent, hex);
        SyncAccentVisual();
        if (!_loading) ApplyAndPersist();
    }

    /// <summary>色块按钮与 hex 回显同步当前 _accent。</summary>
    private void SyncAccentVisual()
    {
        if (AccentSwatch is null) return;   // InitializeComponent 期间

        AccentSwatch.Background = _accent is null ? null : TokenBrushes.Brush(_accent);
        AccentHexLabel.Text = _accent ?? "默认";
    }

    /// <summary>HSV 视图的指针与底色同步当前 HSV 值。</summary>
    private void SyncHsvVisual()
    {
        var hueBrush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(
                (byte)ColorMath.HsvToRgb(_hsvH, 1, 1).R,
                (byte)ColorMath.HsvToRgb(_hsvH, 1, 1).G,
                (byte)ColorMath.HsvToRgb(_hsvH, 1, 1).B));
        SvBase.Fill = hueBrush;

        SvDotTransform.X = _hsvS * SvSquare.Width - 6;
        SvDotTransform.Y = (1 - _hsvV) * SvSquare.Height - 6;
        HueThumbTransform.X = _hsvH / 360.0 * HueBar.Width - 3;
    }

    private void OnAnimationsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ApplyAndPersist();
    }

    private void OnHideOnFocusLossChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyAndPersist();
    }

    /// <summary>
    /// 右键菜单开关。
    ///
    /// 这里只改配置；真正的注册/注销由常驻装配在配置生效后执行 ——
    /// 注册表读写与失败提示都在那一层，面板不必知道注册表长什么样。
    /// </summary>
    private void OnShellMenuChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyAndPersist();
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        AutoStart.SetEnabled(AutoStartBox.IsChecked == true);

        // 写失败（组策略限制）时把勾选撤回，否则界面会显示一个与实际不符的状态。
        if (AutoStart.IsEnabled != (AutoStartBox.IsChecked == true))
        {
            _loading = true;
            AutoStartBox.IsChecked = AutoStart.IsEnabled;
            _loading = false;
            FooterStatus.Text = "开机自启写入注册表失败（可能被组策略或安全软件限制）。";
            return;
        }

        ApplyAndPersist();
    }

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyAndPersist();
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateCheck is null)
        {
            UpdateStatus.Text = "（更新组件不可用）";
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "正在检查…";
        try
        {
            var status = await _updateCheck();
            UpdateStatus.Text = $"{status}（{DateTime.Now:HH:mm}）";
            _updateReady = status.Contains("已就绪");
            RestartUpdateButton.Visibility = _updateReady
                ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnRestartUpdateClick(object sender, RoutedEventArgs e)
    {
        // 重启由用户显式点击触发（spec §3.4：绝不自动重启）。
        if (_updateReady) _updateRestart?.Invoke();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ConfigStore.DefaultDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FooterStatus.Text = "无法打开文件夹：" + ex.Message;
        }
    }

    private void OnOpenMenuFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = new ConfigStore().MenusPath;
            if (!File.Exists(path))
            {
                SetFooter("menus.json 还不存在（首次改动后会自动生成）。", isError: true);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetFooter("无法打开 menus.json：" + ex.Message, isError: true);
        }
    }

    // ---------------------------------------------------------------- 启动队列
    //
    // 数据流（为什么是"每次改动立即整表保存"）：
    // · 队列编辑是低频操作（加一项、挪一下），攒着不存再点"确定"只会多出一个
    //   "有未保存改动"的状态要维护；即时落盘 + 400ms 热重载把状态数压到最少。
    // · 每次保存成功后**重新从取数回调拉一遍**，而不是继续用手里那份工作副本 ——
    //   只有经过文件往返（并被 Mapper 归一：钳间隔、丢坏项）的数据才算数，
    //   否则 UI 显示的顺序/间隔可能与实际会被执行的不是一回事。

    private Func<IReadOnlyList<LaunchQueueSpec>>? _queueLoad;
    private Func<IReadOnlyList<LaunchQueueSpec>, bool>? _queueSave;

    /// <summary>「关于与更新」分区接缝：check 返回状态文案（含「已就绪」时显示重启按钮）。</summary>
    private Func<Task<string>>? _updateCheck;
    private Action? _updateRestart;
    private bool _updateReady;

    internal void SetUpdateHooks(Func<Task<string>>? check, Action? restart)
    {
        _updateCheck = check;
        _updateRestart = restart;
    }

    /// <summary>气泡引导进入时直达本分区（侧栏最后一项）。</summary>
    public void SelectAboutPage() => NavList.SelectedIndex = NavList.Items.Count - 1;

    /// <summary>工作副本（深拷贝：改本地列表时不能碰 ConfigStore.Queues 里那份）。</summary>
    private List<LaunchQueueSpec> _queues = [];

    /// <summary>正在重建队列界面。期间控件产生的事件（ComboBox 选择变化、文本框赋值）都不算用户操作。</summary>
    private bool _queueSyncing;

    /// <summary>改名框是否处于打开态。失焦提交时据此判断"这一下是不是该提交"。</summary>
    private bool _queueRenameOpen;

    /// <summary>
    /// 注入队列的取数/保存回调（由 OverlayController 转发，实际落到 ConfigStore）。
    /// 传 null 表示"队列编辑不可用"（探针或未装配的宿主）：分区显示空状态文案，
    /// 而不是在点按钮时才空引用崩掉。
    /// </summary>
    internal void BindQueues(
        Func<IReadOnlyList<LaunchQueueSpec>>? load,
        Func<IReadOnlyList<LaunchQueueSpec>, bool>? save)
    {
        _queueLoad = load;
        _queueSave = save;

        // 面板早于绑定被创建时，绑定到来的这一刻就补一次 —— 否则用户要先切走再切回来才看得到数据。
        if (PageQueues is { IsVisible: true }) LoadQueuesFromSource();
    }

    /// <summary>从取数回调拉最新列表，替换工作副本后整页重绘。</summary>
    private void LoadQueuesFromSource()
    {
        if (QueueItemsPanel is null) return;

        IReadOnlyList<LaunchQueueSpec> source = [];
        if (_queueLoad is not null)
        {
            try
            {
                source = _queueLoad();
            }
            catch (Exception ex)
            {
                CrashLog.Write("读取启动队列失败", ex);
                SetFooter("读取启动队列失败：" + ex.Message, isError: true);
            }
        }

        _queues = source.Select(CloneQueue).ToList();
        RenderQueuePage();
    }

    /// <summary>
    /// 深拷贝一套队列。record 的 with 只换引用，Items 仍是同一个列表 ——
    /// 工作的这几项（移动、移除、追加）只要有一处忘了"先复制"，就会改到 ConfigStore 手里的
    /// 运行期队列，表现是"还没保存，托盘菜单里的名字已经变了"。
    /// </summary>
    private static LaunchQueueSpec CloneQueue(LaunchQueueSpec queue) => queue with { Items = queue.Items.ToList() };

    /// <summary>改其中一处留下的同一个"先复制"讲究：name/interval/items 任给其一，其余原样带过去。</summary>
    private static LaunchQueueSpec CloneQueue(
        LaunchQueueSpec queue, string? name = null, int? intervalMs = null, List<MenuItemSpec>? items = null) => new(
        queue.Id,
        name ?? queue.Name,
        intervalMs ?? queue.IntervalMs,
        items ?? queue.Items.ToList());

    /// <summary>当前选中的那一套；没有任何队列时为 null。</summary>
    private LaunchQueueSpec? CurrentQueue(int index) =>
        index >= 0 && index < _queues.Count ? _queues[index] : null;

    private int SelectedQueueIndex => Math.Clamp(QueueSelect.SelectedIndex, 0, Math.Max(0, _queues.Count - 1));

    private void RenderQueuePage()
    {
        _queueSyncing = true;
        try
        {
            var usable = _queueLoad is not null && _queueSave is not null;
            var hasQueues = usable && _queues.Count > 0;

            // 头部下拉：按位置填（选中位置在这里保住；重建过程中 SelectionChanged 被 _queueSyncing 挡住）。
            var previous = QueueSelect.SelectedIndex;
            QueueSelect.Items.Clear();
            foreach (var queue in _queues) QueueSelect.Items.Add(queue.Name);
            if (hasQueues) QueueSelect.SelectedIndex = Math.Clamp(previous, 0, _queues.Count - 1);

            QueueSelect.Visibility = hasQueues && !_queueRenameOpen ? Visibility.Visible : Visibility.Collapsed;
            QueueNameBox.Visibility = hasQueues && _queueRenameOpen ? Visibility.Visible : Visibility.Collapsed;

            QueueSelect.IsEnabled = usable;
            QueueRenameButton.IsEnabled = hasQueues;
            QueueDeleteButton.IsEnabled = hasQueues;

            var current = hasQueues ? CurrentQueue(SelectedQueueIndex) : null;

            // 空状态其实是三档：没有任何队列 / 当前队列没有条目 / 队列编辑根本不可用。
            // 前两档用户能自己解决，第三档只有一个"为什么点不动"的说明 —— 所以分开成两个文案位。
            QueueNoQueues.Visibility = !usable ? Visibility.Visible : Visibility.Collapsed;
            QueueNoQueuesLabel.Text = "队列编辑不可用";

            QueueNoItems.Visibility = usable && !hasQueues ? Visibility.Visible : Visibility.Collapsed;
            QueueNoQueuesButton.Visibility = usable && !hasQueues ? Visibility.Visible : Visibility.Collapsed;
            QueueEmptyItems.Visibility = hasQueues && current is { Items.Count: 0 }
                ? Visibility.Visible
                : Visibility.Collapsed;

            QueueListBorder.Visibility = current is not null ? Visibility.Visible : Visibility.Collapsed;
            QueueAddRow.Visibility = current is not null ? Visibility.Visible : Visibility.Collapsed;

            QueueItemsPanel.Children.Clear();
            if (current is not null)
            {
                for (var i = 0; i < current.Items.Count; i++)
                    QueueItemsPanel.Children.Add(BuildQueueRow(current.Items[i], i, current.Items.Count));
            }

            QueueAddButton.IsEnabled = current is not null && current.Items.Count < LaunchQueuesMapper.MaxItems;
            QueueAddButton.ToolTip = current is not null && current.Items.Count >= LaunchQueuesMapper.MaxItems
                ? $"单套队列最多 {LaunchQueuesMapper.MaxItems} 项"
                : "选一个系统位置、文件或文件夹加进队列末尾";

            if (current is not null) QueueIntervalBox.Text = current.IntervalMs.ToString(CultureInfo.InvariantCulture);
            UpdateIntervalNote(current);

            StartQueueNameEditIfPending(current);
        }
        finally
        {
            _queueSyncing = false;
        }
    }

    /// <summary>间隔旁的小字：把"N 项跑完大约多久"直接算出来，省得用户自己乘。</summary>
    private void UpdateIntervalNote(LaunchQueueSpec? queue)
    {
        if (queue is null || queue.Items.Count == 0)
        {
            QueueIntervalNote.Text = "相邻两项的等待毫秒；0 = 同时交给系统，由执行器限速";
            return;
        }

        var total = (queue.Items.Count - 1) * (long)queue.IntervalMs;
        QueueIntervalNote.Text = total >= 1000
            ? $"毫秒 · 相邻两项的等待；{queue.Items.Count} 项跑完约 {total / 1000.0:0.#} 秒"
            : $"毫秒 · 相邻两项的等待；{queue.Items.Count} 项跑完约 {(total == 0 ? "同时" : total + " 毫秒")}";
    }

    /// <summary>
    /// 一行：图标（取不到画首字圆占位，与轮盘同规则）｜名称｜目标灰字｜行尾 ↑ ↓ ✕。
    /// </summary>
    private FrameworkElement BuildQueueRow(MenuItemSpec item, int index, int count)
    {
        var row = new Grid { Height = 30, Background = System.Windows.Media.Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // 悬停底色靠一个铺满的 Border（Grid 本身没有背景可言，设了也拿不到鼠标事件的视觉反馈）。
        row.Children.Add(new Border
        {
            Background = TokenBrushes.Brush("#FFFFFF", 0.045),
            CornerRadius = new CornerRadius(3),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Tag = "hover",
        });
        row.MouseEnter += (_, _) => ((Border)row.Children[0]).Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => ((Border)row.Children[0]).Visibility = Visibility.Collapsed;

        // ---- 图标 ----
        var iconHost = new Grid
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bitmap = ShellIcons.GetScaled(item.IconPath, 16);
        if (bitmap is not null)
        {
            var image = new Image { Source = bitmap, Width = 16, Height = 16 };
            // 取的是按目标尺寸预缩好的位图，显示尺寸 1:1 —— 用廉价的 Linear 即可（同轮盘的取舍）。
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            iconHost.Children.Add(image);
        }
        else
        {
            // 占位圆与轮盘同规则：徽标底色 + 淡描边 + 名称首字。
            iconHost.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Fill = TokenResources.Brush("Color.BadgeFill"),
                Stroke = TokenResources.Brush("Color.Stroke"),
                StrokeThickness = 1,
            });
            iconHost.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Label) ? "?" : item.Label[..1],
                Foreground = TokenResources.Brush("Color.Text"),
                FontFamily = (System.Windows.Media.FontFamily)FindResource("Wq.Font.Family"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        Grid.SetColumn(iconHost, 0);
        row.Children.Add(iconHost);

        // ---- 名称 + 目标（目标超长省略，全文在 ToolTip） ----
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = item.Label,
            Foreground = TokenResources.Brush("Color.Text"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Wq.Font.Family"),
            FontSize = TokenResources.Get<double>("Font.BodySize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Label,
        });

        var target = item.Action?.Target ?? "";
        var targetRow = new Grid
        {
            // 小号灰字：与行内其余文字同列宽，过长的路径要从中间截，不能把行撑宽。
            ClipToBounds = true,
        };
        var targetText = new TextBlock
        {
            Text = target,
            Foreground = TokenResources.Brush("Color.TextMuted"),
            FontFamily = (System.Windows.Media.FontFamily)FindResource("Wq.Font.Family"),
            FontSize = TokenResources.Get<double>("Font.SmallSize"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (target.Length > 0) targetText.ToolTip = target;
        targetRow.Children.Add(targetText);
        texts.Children.Add(targetRow);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);

        // ---- 行尾操作 ----
        var ops = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var up = MakeRowOp("↑", "上移", index > 0, () => MoveQueueItem(index - 1));
        up.Margin = new Thickness(0, 0, 2, 0);
        ops.Children.Add(up);

        var down = MakeRowOp("↓", "下移", index < count - 1, () => MoveQueueItem(index + 1));
        down.Margin = new Thickness(0, 0, 2, 0);
        ops.Children.Add(down);

        ops.Children.Add(MakeRowOp("✕", "移出队列", true, () => RemoveQueueItem(index)));
        Grid.SetColumn(ops, 2);
        row.Children.Add(ops);

        // 除最后一行外的分隔线：用一个薄的 Border 贴在行底（放进 Grid 不占列宽以外的空间）。
        if (index < count - 1)
        {
            var separator = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = TokenBrushes.Brush("#FFFFFF", 0.05),
                IsHitTestVisible = false,
            };
            Grid.SetColumnSpan(separator, 3);
            row.Children.Add(separator);
        }

        return row;
    }

    private Button MakeRowOp(string glyph, string tip, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("FlatButton"),
            Content = glyph,
            Padding = new Thickness(5, 0, 5, 0),
            MinWidth = 22,
            FontSize = TokenResources.Get<double>("Font.SmallSize"),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = enabled,
            ToolTip = tip,
        };
        button.Click += (_, _) =>
        {
            // 分区坏了不能带崩整个设置面板（同 PickMenuItemForAdd 的兜底）：这一次"什么都没发生"并留日志。
            try { onClick(); }
            catch (Exception ex)
            {
                CrashLog.Write("启动队列行操作失败", ex);
                SetFooter("操作失败：" + ex.Message, isError: true);
            }
        };
        return button;
    }

    private void MoveQueueItem(int toIndex)
    {
        var from = SelectedQueueIndex;
        var queue = CurrentQueue(from);
        if (queue is null || toIndex < 0 || toIndex >= queue.Items.Count || toIndex == from) return;

        var items = queue.Items.ToList();
        (items[from], items[toIndex]) = (items[toIndex], items[from]);
        _queues[from] = CloneQueue(queue, items: items);
        CommitQueues();
    }

    private void RemoveQueueItem(int index)
    {
        var q = SelectedQueueIndex;
        var queue = CurrentQueue(q);
        if (queue is null || index < 0 || index >= queue.Items.Count) return;

        // 移除不做二次确认：误删的代价只是重新加一项，而每删一次都弹框会把"整理列表"变得很烦。
        var items = queue.Items.ToList();
        items.RemoveAt(index);
        _queues[q] = CloneQueue(queue, items: items);
        CommitQueues();
    }

    /// <summary>整表保存，成功后按文件里的结果重绘（见本节开头的"数据流"）。</summary>
    private void CommitQueues()
    {
        if (_queueSave is null) return;
        if (!_queueSave(_queues)) return;   // 失败提示由保存回调那一层（托盘气泡）负责

        LoadQueuesFromSource();
    }

    // ---- 头部操作 ----

    private void OnQueueSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_queueSyncing) return;
        RenderQueuePage();
    }

    private void OnQueueNewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_queueSave is null) return;

            // id 只是一个键：用时间戳保证不撞（同一毫秒连点两次的概率可以忽略，且 QueueSelect 会挡住）。
            var id = "q" + DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
            var number = 0;
            var name = "新队列";
            while (_queues.Any(q => q.Name == name)) name = "新队列 " + ++number;

            _queues.Add(new LaunchQueueSpec(id, name, LaunchQueuesMapper.DefaultIntervalMs, []));
            if (!_queueSave(_queues)) return;

            LoadQueuesFromSource();

            // 新建之后直接进入改名：名字几乎总要改，先填好省一次点击。
            // 不直接调 BeginQueueRename —— 那时界面还在 _queueSyncing 里，交给重建末尾的挂起标记。
            QueueSelect.SelectedIndex = _queues.FindIndex(q => q.Id == id);
            _queueRenamePending = true;
            RenderQueuePage();
        }
        catch (Exception ex)
        {
            CrashLog.Write("新建启动队列失败", ex);
            SetFooter("新建队列失败：" + ex.Message, isError: true);
        }
    }

    private void OnQueueRenameClick(object sender, RoutedEventArgs e)
    {
        try { BeginQueueRename(); }
        catch (Exception ex)
        {
            CrashLog.Write("启动队列改名失败", ex);
            SetFooter("改名失败：" + ex.Message, isError: true);
        }
    }

    private void BeginQueueRename()
    {
        var queue = CurrentQueue(SelectedQueueIndex);
        if (queue is null) return;

        _queueRenameOpen = true;
        QueueNameBox.Text = queue.Name;
        QueueSelect.Visibility = Visibility.Collapsed;
        QueueNameBox.Visibility = Visibility.Visible;
        QueueNameBox.Focus();
        QueueNameBox.SelectAll();
    }

    /// <summary>提交改名。空白名不提交（名称是唯一让人认出"是哪一套"的东西，空名等于一列无名行）。</summary>
    private void CommitQueueRename()
    {
        if (!_queueRenameOpen) return;

        var index = SelectedQueueIndex;
        var queue = CurrentQueue(index);
        var name = QueueNameBox.Text.Trim();
        var unchanged = queue is null || name.Length == 0 || name == queue.Name;

        EndQueueRename();

        if (unchanged) return;

        _queues[index] = CloneQueue(queue!, name: name);
        CommitQueues();
    }

    private void CancelQueueRename() => EndQueueRename();

    private void EndQueueRename()
    {
        _queueRenameOpen = false;
        QueueNameBox.Visibility = Visibility.Collapsed;
        QueueSelect.Visibility = _queues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>新建后要立刻进改名态，但那一轮重建里 _queueSyncing 还没放开。挂一个"待改名"标记。</summary>
    private bool _queueRenamePending;

    private void StartQueueNameEditIfPending(LaunchQueueSpec? current)
    {
        if (!_queueRenamePending) return;
        _queueRenamePending = false;
        if (current is not null) BeginQueueRename();
    }

    private void OnQueueNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitQueueRename();
            // Enter 后把焦点交还面板：不然焦点还在隐藏的框里，下一次按键会被吞。
            QueueSelect.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            // **必须吃掉 Esc**：面板的 PreviewKeyDown 会把 Esc 当成"关闭面板"。
            e.Handled = true;
            CancelQueueRename();
            QueueSelect.Focus();
        }
    }

    private void OnQueueNameLostFocus(object sender, RoutedEventArgs e)
    {
        // 提交前先看焦点去哪：如果失焦是因为按了 Esc（CancelQueueRename 已经关掉编辑态），这里就是空的。
        if (_queueRenameOpen) CommitQueueRename();
    }

    private void OnQueueDeleteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var index = SelectedQueueIndex;
            var queue = CurrentQueue(index);
            if (queue is null || _queueSave is null) return;

            var answer = MessageBox.Show(
                this,
                $"删除队列「{queue.Name}」？这一操作会立即写盘。",
                "WinQuick 启动队列",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            _queues.RemoveAt(index);
            if (!_queueSave(_queues)) return;

            LoadQueuesFromSource();
        }
        catch (Exception ex)
        {
            CrashLog.Write("删除启动队列失败", ex);
            SetFooter("删除队列失败：" + ex.Message, isError: true);
        }
    }

    // ---- 条目 / 间隔 ----

    /// <summary>「添加…」：走既有的选择位置流程（系统位置 + 浏览文件 + 浏览文件夹都在里面）。</summary>
    private void OnQueueAddClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var index = SelectedQueueIndex;
            var queue = CurrentQueue(index);
            if (queue is null || queue.Items.Count >= LaunchQueuesMapper.MaxItems) return;
            if (AddQueueItemRequested is null) return;

            var item = AddQueueItemRequested();
            if (item is null) return;   // 用户取消

            // 追加到末尾：队列的语义就是"按顺序"，新加的自然是最后跑的那一个。
            var items = queue.Items.ToList();
            items.Add(item);
            _queues[index] = CloneQueue(queue, items: items);
            CommitQueues();
        }
        catch (Exception ex)
        {
            CrashLog.Write("向启动队列添加条目失败", ex);
            SetFooter("添加失败：" + ex.Message, isError: true);
        }
    }

    /// <summary>打开「添加」选择界面（由 OverlayController 提供），返回用户选中的项；取消返回 null。</summary>
    internal Func<MenuItemSpec?>? AddQueueItemRequested { get; set; }

    private void OnQueueIntervalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitQueueInterval();
        }
    }

    private void OnQueueIntervalLostFocus(object sender, RoutedEventArgs e) => CommitQueueInterval();

    private void CommitQueueInterval()
    {
        try
        {
            var index = SelectedQueueIndex;
            var queue = CurrentQueue(index);
            if (queue is null || _queueSyncing) return;

            if (!int.TryParse(QueueIntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
            {
                // 非数字/空：不提交，把框里的值退回当前值（否则框里会一直留着一串无效字符）。
                QueueIntervalBox.Text = queue.IntervalMs.ToString(CultureInfo.InvariantCulture);
                return;
            }

            var clamped = Math.Clamp(raw, LaunchQueuesMapper.MinIntervalMs, LaunchQueuesMapper.MaxIntervalMs);
            if (clamped != raw) QueueIntervalBox.Text = clamped.ToString(CultureInfo.InvariantCulture);

            if (clamped == queue.IntervalMs) return;

            _queues[index] = CloneQueue(queue, intervalMs: clamped);
            CommitQueues();
        }
        catch (Exception ex)
        {
            CrashLog.Write("设置启动队列间隔失败", ex);
            SetFooter("设置间隔失败：" + ex.Message, isError: true);
        }
    }

    private void SetFooter(string text, bool isError)
    {
        FooterStatus.Text = text;
        FooterStatus.Foreground = TokenResources.Brush(isError ? "Color.Danger" : "Color.TextMuted");
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        LoadFrom(new AppConfig(), notify: true);
        FooterStatus.Text = "已恢复为默认值。";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- 应用

    /// <summary>
    /// 从当前控件组装配置，校验后立刻应用并（去抖）持久化。
    ///
    /// 校验失败时**只提示、不应用** —— 比如用户正在把延迟从 200 改成 300，
    /// 中途会出现"3"这种瞬间的非法值，不能让它生效，也不能把配置写坏。
    /// </summary>
    private void ApplyAndPersist()
    {
        var config = BuildConfig();

        if (config is null)
        {
            // 输入还不完整（比如延迟框是空的或半截数字）：保持现状，不动配置。
            return;
        }

        var problems = config.Validate();
        if (problems.Count > 0)
        {
            FooterStatus.Text = problems[0];
            FooterStatus.Foreground = TokenResources.Brush("Color.Danger");
            return;
        }

        FooterStatus.Text = "改动已生效并自动保存。";
        FooterStatus.Foreground = TokenResources.Brush("Color.TextMuted");

        _apply(config);
    }

    private AppConfig? BuildConfig()
    {
        // 高亮色来自调色盘（_accent 要么是完整 #RRGGBB、要么是 null），没有半截输入态。
        return new AppConfig
        {
            Version = 1,
            Hotkey = _pendingHotkey?.ToString() ?? _original.Hotkey,
            AllowHookFallback = _original.AllowHookFallback,
            ActiveMenu = _original.ActiveMenu,
            Appearance = new AppearanceConfig
            {
                DimOpacity = ReadDimOpacity(),
                AccentColor = _accent,
                RecentAccentColors = _recent,
                // 皮肤/形态不在本面板编辑（换装走皮肤面板），但必须沿用 _original：
                // 漏掉会被 new 的默认 null 覆盖 —— 打开一次设置并保存，皮肤就被静默重置。
                Skin = _original.Appearance.Skin,
                Form = _original.Appearance.Form,
            },
            Behavior = new BehaviorConfig
            {
                // 悬停延迟的界面已删（悬停不再展开，这两项没有效果）。
                // 沿用 _original 的值：老配置里的显式设置原样保留，不因为打开一次设置面板就被清掉。
                HoverOpenDelayMs = _original.Behavior.HoverOpenDelayMs,
                HoverCloseDelayMs = _original.Behavior.HoverCloseDelayMs,
                AnimationsEnabled = AnimationsBox.SelectedIndex switch
                {
                    1 => true,
                    2 => false,
                    _ => null,
                },
                HideOnFocusLoss = HideOnFocusLossBox.IsChecked == true,
            },
            Startup = new StartupConfig { AutoStart = AutoStartBox.IsChecked == true },
            Updates = new UpdateConfig { AutoCheck = AutoUpdateBox.IsChecked == true },
            ShellMenu = ShellMenuBox.IsChecked == true,
        };
    }

    private double? ReadDimOpacity() =>
        // 滑块回到默认值时写回 null，表示"沿用设计基线"。
        // 否则用户只是打开面板看一眼，配置里就多出一个显式值，
        // 从此设计基线再改也影响不到他。
        Math.Abs(DimSlider.Value - DefaultDimOpacity) < 0.001 ? null : DimSlider.Value;

    // ---------------------------------------------------------------- 测试接缝
    // 供 --settings-probe 使用。刻意做小：只暴露"设几个值"和"执行一次应用"。

    internal void SetHotkeyForTest(HotkeySpec spec)
    {
        _pendingHotkey = spec;
        HotkeyBox.Text = spec.ToString();
    }

    /// <summary>探针用：直接选一个预设下拉项（走真实的 OnPresetChanged 路径）。</summary>
    internal void SelectPresetForTest(string preset)
    {
        _loading = false;
        PresetBox.SelectedItem = preset;
    }

    /// <summary>探针用：当前预设下拉选中的项。</summary>
    internal string? SelectedPresetForTest => PresetBox.SelectedItem as string;
    internal void SetDimOpacityForTest(double value) => DimSlider.Value = value;

    /// <summary>
    /// 接缝：直接设置当前高亮色。完整 #RRGGBB（# 可省）才接受 —— 半截/非法值不动状态，
    /// 与调色盘「非法值进不来」的语义一致（空串 = 清成默认）。
    /// </summary>
    internal void SetAccentForTest(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) { _accent = null; SyncAccentVisual(); return; }

        var normalized = text.StartsWith('#') ? text : "#" + text;
        if (!ColorMath.TryParseHex(normalized, out _, out _, out _)) return;

        _accent = normalized.ToUpperInvariant();
        SyncAccentVisual();
    }

    /// <summary>接缝：走真实的提交路径（进最近使用并应用）。</summary>
    internal void CommitAccentForTest(string hex) => CommitAccent(hex);

    /// <summary>接缝：当前最近使用列表。</summary>
    internal IReadOnlyList<string> RecentAccentColorsForTest => _recent;

    internal void InvokeApplyForTest() => ApplyAndPersist();

    /// <summary>探针用：把"用户拖过"这个状态直接按上（没法在探针里模拟真实的 DragMove）。</summary>
    internal void MarkPlacedByUserForTest() => PlacedByUser = true;

    // ---- 刷新闪烁探针用的接缝（--flicker-probe）----------------------------
    // 这些动作**只改内容、不改尺寸**，正是"纯内容刷新"的样子。
    // 面板高度会不会跟着变，直接决定分层窗口要不要重建表面 —— 所以探针
    // 每一步都把高度一起打出来，而不是只报一个"闪没闪"。
    internal void SetFooterForProbe(string text) => SetFooter(text, isError: false);
    internal void SetHotkeyStatusForProbe(string text) => SetHotkeyStatus(text, isError: false);
    internal double PanelHeightForProbe => ActualHeight;
    internal double PanelWidthForProbe => ActualWidth;

    internal string HotkeyStatusTextForTest => HotkeyStatus.Text;

    internal string FooterStatusTextForTest => FooterStatus.Text;

    internal AppConfig? BuildConfigForTest() => BuildConfig();

}
