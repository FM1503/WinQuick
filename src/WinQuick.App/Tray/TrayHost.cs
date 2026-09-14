using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using WinQuick.App.Hosting;

namespace WinQuick.App.Tray;

/// <summary>
/// 托盘图标与右键菜单。常驻程序的主要可见入口 —— 用户找不到主窗口，
/// 所有"退出 / 设置 / 有没有在跑"都要靠这里。
///
/// 托盘菜单文案用中文，与项目其余面向用户的部分保持一致。
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly OverlayController _overlay;
    private MenuItem _autoStartItem = null!;
    private bool _disposed;

    public TrayHost(OverlayController overlay)
    {
        _overlay = overlay;

        _icon = new TaskbarIcon
        {
            ToolTipText = "WinQuick —— 按热键唤出快捷轮盘",
            Visibility = Visibility.Visible,
            ContextMenu = BuildMenu(),
        };

        _icon.TrayLeftMouseUp += (_, _) => _overlay.Show();
        _icon.TrayMouseDoubleClick += (_, _) => _overlay.Show();

        try
        {
            _icon.Icon = System.Drawing.SystemIcons.Application;
        }
        catch (Exception ex)
        {
            // M4 会换成正式图标。M1 阶段用系统图标，缺失不该让托盘整体失效。
            Debug.WriteLine("托盘图标加载失败: " + ex.Message);
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MakeItem("唤出轮盘", () => _overlay.Show()));
        menu.Items.Add(new Separator());

        _autoStartItem = MakeItem("开机自启", ToggleAutoStart);
        _autoStartItem.IsCheckable = true;
        _autoStartItem.IsChecked = AutoStart.IsEnabled;
        menu.Items.Add(_autoStartItem);

        menu.Items.Add(MakeItem("打开配置文件夹", OpenConfigFolder));
        menu.Items.Add(MakeItem("查看崩溃日志", OpenLogFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("退出", ExitApplication));

        return menu;
    }

    private static MenuItem MakeItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ToggleAutoStart()
    {
        var target = !AutoStart.IsEnabled;
        var ok = AutoStart.SetEnabled(target);

        // 写注册表可能失败（组策略限制）。失败时把勾选状态撤回，
        // 不能显示一个与实际不符的勾 —— 那会让人以为设置生效了。
        _autoStartItem.IsChecked = ok && target;

        if (!ok)
        {
            MessageBox.Show(
                "写入开机自启项失败。可能是组策略或安全软件限制了注册表写入。",
                "WinQuick", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenConfigFolder()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinQuick");
        OpenFolder(dir);
    }

    private static void OpenLogFolder() => OpenFolder(CrashLog.LogDirectory);

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("无法打开文件夹：" + ex.Message, "WinQuick",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExitApplication()
    {
        Application.Current.Shutdown(0);
    }

    /// <summary>
    /// 托盘气泡提示。用于「热键被占用」「钩子需要确认」这类必须让用户知道的降级情况 ——
    /// 热键是唯一入口，静默失效会被当成程序坏了。
    /// </summary>
    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message);
        }
        catch (Exception ex)
        {
            // 通知系统可能被策略关闭（专注助手、组策略）。提示发不出去不该影响主流程，
            // 崩溃日志里已经有一份记录。
            Debug.WriteLine("托盘通知发送失败: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Dispose();
    }
}
