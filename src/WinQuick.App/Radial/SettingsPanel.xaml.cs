using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using WinQuick.App.Config;
using WinQuick.App.Design;
using WinQuick.App.Hosting;
using WinQuick.App.Settings;
using WinQuick.Core.Config;
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

    /// <summary>
    /// 正在按程序化方式改控件（初始化或恢复默认）。
    /// 期间不能触发"改动即应用"，否则打开面板这一个动作本身就会写一次配置。
    /// </summary>
    private bool _loading;

    public SettingsPanel(
        AppConfig current,
        Func<HotkeySpec, (bool Available, int ErrorCode)> probeHotkey,
        Action<AppConfig> apply)
    {
        InitializeComponent();

        _original = current;
        _probeHotkey = probeHotkey;
        _apply = apply;

        LoadFrom(current, notify: false);
    }

    /// <summary>用户要求关闭设置（点 ✕ 或按 Esc）。</summary>
    public event EventHandler? CloseRequested;

    internal nint HandleValue => new WindowInteropHelper(this).Handle;

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
}
