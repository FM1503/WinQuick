using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WinQuick.App.Config;
using WinQuick.App.Design;
using WinQuick.App.Hosting;
using WinQuick.App.Interop;
using WinQuick.App.Settings;
using WinQuick.Core.Config;
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
    private readonly Action<MenuSpec> _applyMenu;
    private MenuSpec _menu;

    private HotkeySpec? _pendingHotkey;
    private bool _capturing;

    /// <summary>
    /// 正在按程序化方式改控件（初始化或恢复默认）。
    /// 期间不能触发"改动即应用"，否则打开面板这一个动作本身就会写一次配置。
    /// </summary>
    private bool _loading;

    public SettingsPanel(
        AppConfig current,
        MenuSpec menu,
        Func<HotkeySpec, (bool Available, int ErrorCode)> probeHotkey,
        Action<AppConfig> applyConfig,
        Action<MenuSpec> applyMenu)
    {
        InitializeComponent();

        _original = current;
        _menu = menu;
        _probeHotkey = probeHotkey;
        _apply = applyConfig;
        _applyMenu = applyMenu;

        LoadFrom(current, notify: false);
        RebuildMenuList();
    }

    /// <summary>用户要求关闭设置（点 ✕ 或按 Esc）。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// 即将弹出一个模态系统对话框 / 对话框已关闭。
    ///
    /// 为什么要通知外面：对话框创建时并不知道我们的窗口是置顶的，
    /// 需要外部把 z 序重申的频率临时调高，否则它会先被遮罩盖住最多一个重申周期
    /// （默认 800ms）—— 用户看到的就是"对话框闪一下才出来"。
    /// </summary>
    public event EventHandler? ModalDialogOpening;
    public event EventHandler? ModalDialogClosed;

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
        SetHotkeyStatus("点「测试」试一下这个组合能不能注册。", isError: false);

        // 与默认值相同时滑块回到默认位置，并在文案里标明"默认" ——
        // 不然用户分不清"我改过没有"。
        DimSlider.Value = config.Appearance.DimOpacity ?? DefaultDimOpacity;
        UpdateDimLabel(config.Appearance.DimOpacity);

        AccentBox.Text = config.Appearance.AccentColor ?? "";

        OpenDelayBox.Text = config.Behavior.HoverOpenDelayMs?.ToString(CultureInfo.InvariantCulture) ?? "";
        CloseDelayBox.Text = config.Behavior.HoverCloseDelayMs?.ToString(CultureInfo.InvariantCulture) ?? "";

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

        _loading = false;

        if (notify) ApplyAndPersist();
    }

    private void UpdateDimLabel(double? explicitValue) =>
        DimValue.Text = explicitValue is null
            ? $"{DimSlider.Value * 100:0}% 默认"
            : $"{DimSlider.Value * 100:0}%";

    /// <summary>进入设置时播放与轮盘一致的入场动画，观感上是覆盖层的一部分。</summary>
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

        var scale = new System.Windows.Media.ScaleTransform(0.94, 0.94);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;

        var scaleAnimation = new DoubleAnimationUsingKeyFrames();
        scaleAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0.94, KeyTime.FromTimeSpan(TimeSpan.Zero)));
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

        var suggestion = HotkeySpec.SuggestIfReserved(spec);
        SetHotkeyStatus(suggestion ?? "点「测试」试一下能不能注册。", isError: false);

        // 录制后不自动应用 —— 用户可能只是在试。误改一个正在生效的热键会让工具突然"消失"。
    }

    private void OnTestHotkeyClick(object sender, RoutedEventArgs e)
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

    private void OnAccentChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading) return;
        ApplyAndPersist();
    }

    private void OnDelayChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading) return;
        ApplyAndPersist();
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

    // ---------------------------------------------------------------- 菜单项编辑

    /// <summary>与 Core 的 RadialLayout.MaxSectors 一致。</summary>
    private const int MaxMenuItems = 12;

    /// <summary>
    /// 重建菜单项列表。
    ///
    /// 用代码搭行而不是 XAML 数据模板：每行需要一个可编辑的文本框、一段带省略号的路径、
    /// 一个删除按钮，行数还随菜单变化 —— 代码搭起来比模板加命令绑定短得多，
    /// 也更容易看清实际结构。
    /// </summary>
    private void RebuildMenuList()
    {
        MenuItemList.Children.Clear();
        MenuSummary.Text = $"当前 {_menu.Items.Count} 项，最多 {MaxMenuItems} 项。";

        for (var i = 0; i < _menu.Items.Count; i++)
            MenuItemList.Children.Add(BuildMenuRow(i, _menu.Items[i]));
    }

    private UIElement BuildMenuRow(int index, MenuItemSpec item)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 名称可直接改：自动取的名字有时很长（「Windows 命令处理程序」），
        // 强制用户回 JSON 里改会很难用。
        var label = new TextBox
        {
            Text = item.Label,
            Style = (Style)FindResource("Field"),
            Margin = new Thickness(0, 0, 6, 0),
        };
        label.LostFocus += (_, _) => OnLabelEdited(index, label.Text);
        label.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Keyboard.ClearFocus();
            e.Handled = true;
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        // 目标只读显示，过长用省略号截断，完整值放 tooltip。
        var target = new TextBlock
        {
            Text = DescribeTarget(item),
            Foreground = TokenResources.Brush("Color.TextMuted"),
            FontFamily = TokenResources.Get<System.Windows.Media.FontFamily>("Font.Family"),
            FontSize = TokenResources.Get<double>("Font.SmallSize"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Action?.Target ?? "(无动作，点开是子菜单)",
        };
        Grid.SetColumn(target, 1);
        row.Children.Add(target);

        var remove = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "从菜单中移除",
        };
        remove.Click += (_, _) => OnRemoveItemClick(index);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        return row;
    }

    private static string DescribeTarget(MenuItemSpec item) =>
        item.HasChildren ? $"子菜单（{item.Children.Count} 项）" : item.Action?.Target ?? "(无动作)";

    private void OnLabelEdited(int index, string newLabel)
    {
        if (index < 0 || index >= _menu.Items.Count) return;

        var trimmed = newLabel.Trim();
        if (trimmed.Length == 0)
        {
            // 空名称会让菜单里出现一个认不出的项 —— 拒绝并还原成原值。
            SetFooter("名称不能为空。", isError: true);
            RebuildMenuList();
            return;
        }

        if (trimmed == _menu.Items[index].Label) return;

        var items = _menu.Items.ToList();
        items[index] = items[index] with { Label = trimmed };
        _menu = _menu with { Items = items };

        _applyMenu(_menu);
        SetFooter("名称已改。", isError: false);
    }

    private void OnRemoveItemClick(int index)
    {
        if (index < 0 || index >= _menu.Items.Count) return;

        if (_menu.Items.Count <= 1)
        {
            SetFooter("至少要保留一项，否则轮盘会是空的。", isError: true);
            return;
        }

        var items = _menu.Items.ToList();
        items.RemoveAt(index);
        _menu = _menu with { Items = items };

        _applyMenu(_menu);
        RebuildMenuList();
        SetFooter("已移除。", isError: false);
    }

    /// <summary>
    /// 用文件浏览器选一个程序加进菜单。
    ///
    /// 名称从程序的**版本信息**取（<see cref="ShellNames"/>），与内置默认菜单同源 ——
    /// 这样用户加进来的项和预置项长得一致、自动跟随系统语言，也不必自己想名字。
    /// 之后可以直接在列表里改。
    /// </summary>
    private void OnAddProgramClick(object sender, RoutedEventArgs e)
    {
        if (_menu.Items.Count >= MaxMenuItems)
        {
            SetFooter($"菜单最多 {MaxMenuItems} 项（再多扇区会细到点不中）。"
                      + "可以先移除几项，或把新项放进子菜单。", isError: true);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要加入菜单的程序",
            Filter = "程序与快捷方式 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        // 以面板为所有者：面板是置顶的，有所有者的对话框会跟着置顶，不会被遮罩挡住。
        //
        // 但"跟着置顶"不会自动发生 —— 官方语义是「一个窗口**变成** topmost 时
        // 其被拥有窗口才连带」，而面板早就是 topmost 了。所以对话框仍是非置顶的，
        // 需要外部的 z 序重申把它补上去。这里通知外部临时提高重申频率。
        ModalDialogOpening?.Invoke(this, EventArgs.Empty);
        bool? accepted;
        try
        {
            accepted = dialog.ShowDialog(this);
        }
        finally
        {
            ModalDialogClosed?.Invoke(this, EventArgs.Empty);
        }

        if (accepted != true) return;

        AddProgram(dialog.FileName);
    }

    private void AddProgram(string path)
    {
        var label = ShellNames.Get(path);

        var items = _menu.Items.ToList();
        items.Add(new MenuItemSpec
        {
            Label = label,
            IconPath = path,
            Action = MenuAction.Launch(path),
        });
        _menu = _menu with { Items = items };

        _applyMenu(_menu);
        RebuildMenuList();
        SetFooter($"已加入「{label}」。", isError: false);
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
        var open = ParseDelay(OpenDelayBox.Text);
        var close = ParseDelay(CloseDelayBox.Text);

        // null 表示"输入还没完成或非法"，此时不应用 —— 见 ApplyAndPersist 的说明。
        if (open.IsInvalid || close.IsInvalid) return null;

        var accent = AccentBox.Text.Trim();
        if (accent.Length > 0)
        {
            accent = accent.StartsWith('#') ? accent : "#" + accent;
            // 正在输入中的半截色值（如 "#00"）不该被应用，也不该被当成错误刷屏。
            if (!LooksLikeCompleteHex(accent)) return null;
        }

        return new AppConfig
        {
            Version = 1,
            Hotkey = _pendingHotkey?.ToString() ?? _original.Hotkey,
            AllowHookFallback = _original.AllowHookFallback,
            ActiveMenu = _original.ActiveMenu,
            Appearance = new AppearanceConfig
            {
                DimOpacity = ReadDimOpacity(),
                AccentColor = accent.Length == 0 ? null : accent,
            },
            Behavior = new BehaviorConfig
            {
                HoverOpenDelayMs = open.Value,
                HoverCloseDelayMs = close.Value,
                AnimationsEnabled = AnimationsBox.SelectedIndex switch
                {
                    1 => true,
                    2 => false,
                    _ => null,
                },
                HideOnFocusLoss = HideOnFocusLossBox.IsChecked == true,
            },
            Startup = new StartupConfig { AutoStart = AutoStartBox.IsChecked == true },
        };
    }

    private double? ReadDimOpacity() =>
        // 滑块回到默认值时写回 null，表示"沿用设计基线"。
        // 否则用户只是打开面板看一眼，配置里就多出一个显式值，
        // 从此设计基线再改也影响不到他。
        Math.Abs(DimSlider.Value - DefaultDimOpacity) < 0.001 ? null : DimSlider.Value;

    private static bool LooksLikeCompleteHex(string text)
    {
        var body = text.TrimStart('#');
        return body.Length == 6 && body.All(Uri.IsHexDigit);
    }

    private (int? Value, bool IsInvalid) ParseDelay(string text)
    {
        // 留空 = 沿用设计基线。
        if (string.IsNullOrWhiteSpace(text)) return (null, false);

        if (int.TryParse(text.Trim(), out var value)) return (value, false);

        // 半截输入（比如刚开始输入、或删除到只剩符号）：不应用也不报错。
        // 报错会在用户打字的过程中不停闪红字。
        return (null, true);
    }

    // ---------------------------------------------------------------- 测试接缝
    // 供 --settings-probe 使用。刻意做小：只暴露"设几个值"和"执行一次应用"。

    internal void SetHotkeyForTest(HotkeySpec spec)
    {
        _pendingHotkey = spec;
        HotkeyBox.Text = spec.ToString();
    }

    internal void SetDelaysForText(string open, string close)
    {
        OpenDelayBox.Text = open;
        CloseDelayBox.Text = close;
    }

    internal void SetDimOpacityForTest(double value) => DimSlider.Value = value;

    internal void SetAccentForTest(string? value) => AccentBox.Text = value ?? "";

    internal void InvokeApplyForTest() => ApplyAndPersist();

    internal string HotkeyStatusTextForTest => HotkeyStatus.Text;

    internal string FooterStatusTextForTest => FooterStatus.Text;

    internal AppConfig? BuildConfigForTest() => BuildConfig();

    internal int MenuItemCountForTest => _menu.Items.Count;

    internal IReadOnlyList<MenuItemSpec> MenuItemsForTest => _menu.Items;

    /// <summary>模拟"用文件浏览器选了一个程序"的结果（不弹对话框）。</summary>
    internal void AddProgramForTest(string path) => AddProgram(path);

    internal void RemoveItemForTest(int index) => OnRemoveItemClick(index);

    /// <summary>打开"添加程序"的文件对话框。探针用它验证对话框的层级。</summary>
    internal void OpenAddProgramDialogForTest() =>
        OnAddProgramClick(this, new RoutedEventArgs());
}
