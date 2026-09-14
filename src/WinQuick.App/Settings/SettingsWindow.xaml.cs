using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinQuick.App.Config;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;
using WinQuick.Core.Hotkey;

namespace WinQuick.App.Settings;

/// <summary>
/// 设置界面。
///
/// 这个窗口存在的首要理由不是"少改 JSON"，而是**热键的实时测试**：
/// 热键是这个工具唯一的入口，而 Windows 上大量组合会被系统或其他程序占用
/// （实测 Win+W、Win+A 都返回 1409）。用户需要敲一个组合就立刻知道能不能用，
/// 而不是改完配置、重启、发现没反应、再猜哪里错了。
///
/// 刻意不做成 MVVM：这是一个十几项的表单对话框，引入 ViewModel + 绑定 + 命令的
/// 样板代码，收益抵不上它带来的间接层。code-behind 直接读写控件更短也更好读。
/// </summary>
public partial class SettingsWindow : Window
{
    private const double DefaultDimOpacity = 0.4;

    private readonly AppConfig _original;
    private readonly Func<HotkeySpec, (bool Available, int ErrorCode)> _probeHotkey;
    private readonly Func<AppConfig, (bool Ok, string? Error)> _save;

    private HotkeySpec? _pendingHotkey;
    private bool _capturing;
    private string? _parseError;

    public SettingsWindow(
        AppConfig current,
        Func<HotkeySpec, (bool Available, int ErrorCode)> probeHotkey,
        Func<AppConfig, (bool Ok, string? Error)> save)
    {
        InitializeComponent();

        _original = current;
        _probeHotkey = probeHotkey;
        _save = save;

        LoadFrom(current);
    }

    // ---------------------------------------------------------------- 初始化

    private void LoadFrom(AppConfig config)
    {
        _pendingHotkey = HotkeySpec.TryParse(config.Hotkey, out var spec, out _) && spec is not null
            ? spec
            : HotkeySpec.Default;

        HotkeyBox.Text = _pendingHotkey.ToString();
        SetHotkeyStatus("点「应用」试一下这个组合能不能注册。", isError: false);

        // 外观。留空表示"沿用设计基线"，所以这里不显示设计值 ——
        // 显示出来会让用户以为已经显式设过，之后分不清"我到底改没改"。
        DimSlider.Value = config.Appearance.DimOpacity ?? DefaultDimOpacity;
        UpdateDimLabel(config.Appearance.DimOpacity);
        AccentBox.Text = config.Appearance.AccentColor ?? "";

        // 行为
        OpenDelayBox.Text = config.Behavior.HoverOpenDelayMs?.ToString(CultureInfo.InvariantCulture) ?? "";
        CloseDelayBox.Text = config.Behavior.HoverCloseDelayMs?.ToString(CultureInfo.InvariantCulture) ?? "";

        AnimationsBox.Items.Clear();
        AnimationsBox.Items.Add("跟随系统设置");
        AnimationsBox.Items.Add("始终播放");
        AnimationsBox.Items.Add("始终不播放");
        AnimationsBox.SelectedIndex = config.Behavior.AnimationsEnabled switch
        {
            null => 0,
            true => 1,
            false => 2,
        };

        HideOnFocusLossBox.IsChecked = config.Behavior.HideOnFocusLoss;

        // 自启的事实来源是注册表（用户也可能从托盘菜单改），所以读注册表而不是配置。
        AutoStartBox.IsChecked = AutoStart.IsEnabled;
    }

    private void UpdateDimLabel(double? explicitValue) =>
        DimValue.Text = explicitValue is null
            ? $"{DimSlider.Value * 100:0}%（默认）"
            : $"{explicitValue * 100:0}%";

    // ---------------------------------------------------------------- 热键

    private void OnHotkeyBoxFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturing) return;

        _capturing = true;
        SetHotkeyStatus("正在等待按键……请按下想要的组合键。", isError: false);
    }

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        // Tab 用来在控件间移动焦点，不能被当成热键录走，否则键盘导航就废了。
        if (e.Key is Key.Tab or Key.Escape)
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
        SetHotkeyStatus(
            suggestion is null ? "按「应用」试一下能不能注册。" : suggestion,
            isError: false);

        // 刻意不在这里自动应用：用户可能只是想看看这个组合长什么样。
        // 误改一个正在生效的热键会让工具突然"消失"，代价比多点一次按钮大。
    }

    private void OnApplyHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (_pendingHotkey is not { } spec) return;

        var (available, errorCode) = _probeHotkey(spec);

        if (available)
        {
            SetHotkeyStatus($"可用。{spec} 能正常注册，按「保存」后生效。", isError: false);
            return;
        }

        var suggestion = HotkeySpec.SuggestIfReserved(spec);
        var reason = errorCode switch
        {
            1409 => "已被系统或其他程序占用",
            87 => "这个组合不合法",
            _ => $"注册失败（错误码 {errorCode}）",
        };

        SetHotkeyStatus(
            $"{spec}：{reason}。" + (suggestion is null
                ? "换一个组合试试。"
                : "Windows 11 上只带 Win 键的组合大多被系统预留 —— " + suggestion),
            isError: true);
    }

    private void SetHotkeyStatus(string text, bool isError)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(0xF0, 0x8A, 0x8A)
            : Color.FromRgb(0x8C, 0xC8, 0x8C));
    }

    // ---------------------------------------------------------------- 外观与杂项

    private void OnDimChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged 在 InitializeComponent 期间也会触发，那时 DimValue 还没赋值。
        if (DimValue is null) return;

        DimValue.Text = $"{e.NewValue * 100:0}%";
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ConfigStore.DefaultDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "无法打开文件夹：" + ex.Message;
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        LoadFrom(new AppConfig());
        SaveStatus.Text = "已恢复为默认值（还没保存）。";
    }

    // ---------------------------------------------------------------- 保存

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveCurrentSettings();

    /// <summary>
    /// 从当前控件值组装配置、校验并保存。
    ///
    /// 抽成独立方法是为了给探针留一个测试接缝：这段「从控件读值 → 组装模型 → 校验 → 落盘」
    /// 是设置界面里风险最高的一环（留空表示沿用设计基线、滑块值等于默认值时写回 null
    /// 等约定都在这里），但它绑着控件、无法直接测。接缝做小一点，好过让这段完全没验证。
    /// </summary>
    internal (bool Ok, string? Message) SaveCurrentSettings()
    {
        _parseError = null;

        var config = new AppConfig
        {
            Version = 1,
            Hotkey = _pendingHotkey?.ToString() ?? _original.Hotkey,
            AllowHookFallback = _original.AllowHookFallback,
            ActiveMenu = _original.ActiveMenu,
            Appearance = new AppearanceConfig
            {
                DimOpacity = ReadDimOpacity(),
                AccentColor = ReadAccentColor(),
            },
            Behavior = new BehaviorConfig
            {
                HoverOpenDelayMs = ReadDelay(OpenDelayBox.Text, "悬停展开延迟"),
                HoverCloseDelayMs = ReadDelay(CloseDelayBox.Text, "悬停收起延迟"),
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

        if (_parseError is { } parseError)
        {
            SaveStatus.Text = parseError;
            return (false, parseError);
        }

        // 校验交给 Core 的同一套规则，不在界面里重写一遍 ——
        // 两处规则迟早分叉（比如"收起必须慢于展开"只在一处改了）。
        var problems = config.Validate();
        if (problems.Count > 0)
        {
            var text = string.Join("\n", problems);
            SaveStatus.Text = text;
            return (false, text);
        }

        var (ok, saveError) = _save(config);
        SaveStatus.Text = ok
            ? "已保存并生效。" + (saveError is null ? "" : "\n注意：" + saveError)
            : "保存失败：" + (saveError ?? "未知原因") + "（已保留上一份有效配置）";

        return (ok, saveError);
    }

    private double? ReadDimOpacity()
    {
        // 滑块值与默认值相同时视为"沿用设计基线"，写回 null。
        // 否则用户只是打开设置看一眼、什么都没动，配置里就会多出一个显式值，
        // 从此设计基线再改也影响不到他 —— 那不是他想要的结果。
        var value = DimSlider.Value;
        return Math.Abs(value - DefaultDimOpacity) < 0.001 ? null : value;
    }

    private string? ReadAccentColor()
    {
        var text = AccentBox.Text.Trim();
        if (text.Length == 0) return null;
        return text.StartsWith('#') ? text : "#" + text;
    }

    private int? ReadDelay(string text, string fieldName)
    {
        // 留空表示沿用设计基线。
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (int.TryParse(text.Trim(), out var value)) return value;

        // 记下错误、由上方统一报出 —— 在这里直接返回会让用户的输入被静默丢弃。
        _parseError ??= $"{fieldName}「{text}」不是整数。";
        return null;
    }

    // ---------------------------------------------------------------- 测试接缝
    // 只给 --settings-probe 用。刻意做得很小：仅暴露"设置几个值"与"执行保存"，
    // 而不是把整个窗口内部开放出去。

    internal void SetHotkeyForTest(HotkeySpec spec)
    {
        _pendingHotkey = spec;
        HotkeyBox.Text = spec.ToString();
    }

    internal void SetDelaysForTest(int? openMs, int? closeMs)
    {
        OpenDelayBox.Text = openMs?.ToString(CultureInfo.InvariantCulture) ?? "";
        CloseDelayBox.Text = closeMs?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    internal void SetDimOpacityForTest(double value) => DimSlider.Value = value;

    internal void SetAccentForTest(string? value) => AccentBox.Text = value ?? "";

    internal void SetAutoStartForTest(bool value) => AutoStartBox.IsChecked = value;

    internal string HotkeyStatusTextForTest => HotkeyStatus.Text;

    internal string SaveStatusTextForTest => SaveStatus.Text;

    internal void InvokeHotkeyApplyForTest() => OnApplyHotkeyClick(this, new RoutedEventArgs());
}
