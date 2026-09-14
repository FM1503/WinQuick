using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Radial;

/// <summary>
/// 覆盖层左上角的操作条：设置 与 退出。
///
/// 存在的理由：托盘图标在 Windows 11 上默认被收进溢出区，用户不一定找得到，
/// 而托盘是当时唯一的设置入口与退出入口。热键唤出的覆盖层是用户必然会看到的地方，
/// 把入口放在这里比指望用户去翻托盘可靠得多。
///
/// 「退出」同样重要：托盘图标不可见时，用户原本没有任何退出途径。
///
/// 实现上是独立的小窗口而不是把按钮画在某个已有窗口里：
/// - 遮罩层是原生 Win32 窗口（为了内存，刻意不用 WPF），画按钮就得手写 GDI 绘制与命中测试
/// - 轮盘窗口只有 740×740 且跟着光标走，覆盖不到屏幕角落
/// 所以单独一个小尺寸 WPF 窗口最直接。尺寸很小，内存代价需要实测确认。
/// </summary>
public sealed class OverlayActionBar : Window
{
    private const double BarWidth = 132;
    private const double BarHeight = 38;

    /// <summary>距屏幕边缘的留白。</summary>
    private const int ScreenMargin = 12;

    private bool _stylesApplied;

    public OverlayActionBar()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;          // 绝不抢焦点 —— 焦点要留给轮盘，否则 Esc 失效

        Width = BarWidth;
        Height = BarHeight;

        Content = BuildContent();
        SourceInitialized += (_, _) => ApplyExtendedStyles();
    }

    /// <summary>用户点了「设置」。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>用户点了「退出」。</summary>
    public event EventHandler? ExitRequested;

    internal nint HandleValue => new WindowInteropHelper(this).Handle;

    private UIElement BuildContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        panel.Children.Add(MakeButton("\uE713", "设置", "打开设置界面", () =>
            SettingsRequested?.Invoke(this, EventArgs.Empty)));
        panel.Children.Add(MakeButton("\uE7E8", "退出", "退出 WinQuick", () =>
            ExitRequested?.Invoke(this, EventArgs.Empty)));

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(216, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = panel,
        };
    }

    private static Button MakeButton(string glyph, string text, string tooltip, Action onClick)
    {
        // 图标用 Windows 自带的符号字体，不去画矢量路径 ——
        // 系统字体在任何 DPI 下都清晰，而且省掉一堆几何数据。
        var content = new StackPanel { Orientation = Orientation.Horizontal };

        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            Foreground = Brushes.White,
        });

        content.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
        });

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Template = BuildFlatTemplate(),
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>无边框按钮模板：默认透明，悬停时给一层浅色底。</summary>
    private static ControlTemplate BuildFlatTemplate()
    {
        var template = new ControlTemplate(typeof(Button));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        template.VisualTree = border;

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(56, 255, 255, 255))));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(96, 255, 255, 255))));
        template.Triggers.Add(pressed);

        return template;
    }

    private void ApplyExtendedStyles()
    {
        if (_stylesApplied) return;
        _stylesApplied = true;

        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        ex |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)(uint)ex);
    }

    /// <summary>摆到光标所在屏的左上角。物理像素计算，避免 DIP 换算误差。</summary>
    public void PlaceAtMonitorCorner()
    {
        ApplyExtendedStyles();

        var monitor = MonitorLayout.AtCursor();
        var physicalWidth = (int)Math.Round(BarWidth * monitor.Scale);
        var physicalHeight = (int)Math.Round(BarHeight * monitor.Scale);

        var left = monitor.X + (int)Math.Round(ScreenMargin * monitor.Scale);
        var top = monitor.Y + (int)Math.Round(ScreenMargin * monitor.Scale);

        Left = left / monitor.Scale;
        Top = top / monitor.Scale;

        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        PInvoke.SetWindowPos(hwnd, default, left, top, physicalWidth, physicalHeight,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    public void ReassertTopmost()
    {
        if (!IsVisible) return;
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        PInvoke.SetWindowPos(hwnd, default, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }
}
