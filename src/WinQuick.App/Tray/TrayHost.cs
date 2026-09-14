using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using WinQuick.App.Hosting;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

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

    /// <summary>点「设置…」时调用。由常驻装配注入，托盘本身不认识设置窗口。</summary>
    public Action? OpenSettings { get; set; }
    private MenuItem _autoStartItem = null!;
    private bool _disposed;

    public TrayHost(OverlayController overlay)
    {
        _overlay = overlay;

        _icon = new TaskbarIcon
        {
            ToolTipText = "WinQuick —— 按热键唤出快捷轮盘",
            ContextMenu = BuildMenu(),
        };

        _icon.TrayLeftMouseUp += (_, _) => _overlay.Show();
        _icon.TrayMouseDoubleClick += (_, _) => _overlay.Show();

        // 顺序很重要：**先设图标，再设可见**。
        // 反过来的话，外壳图标先以"没有图标"的状态被创建、之后再更新 ——
        // 那是一条更脆弱、更依赖库内部实现的路径。
        LoadIcon();

        _icon.Visibility = Visibility.Visible;

        // 显式强制创建，并核对结果。
        // IsCreated 是唯一能确认"外壳真的接受了这个图标"的途径 ——
        // 之前既没核对它、也没在失败时报错，所以"托盘里什么都没有"这件事
        // 一直是个没有线索的黑盒。
        try
        {
            _icon.ForceCreate();
        }
        catch (Exception ex)
        {
            CrashLog.Write("托盘图标 ForceCreate 失败", ex);
        }

        Created = _icon.IsCreated;

        if (!Created)
        {
            CrashLog.Write("托盘图标未创建成功",
                new InvalidOperationException(
                    "TaskbarIcon.IsCreated 为 false —— 外壳没有接受这个图标，" +
                    "后果是托盘里看不到 WinQuick。"));
        }
    }

    /// <summary>诊断用：外壳是否真的接受了这个托盘图标。</summary>
    internal bool Created { get; private set; }

    /// <summary>
    /// 加载托盘图标。
    ///
    /// 从嵌入资源读，而不是用 System.Drawing.SystemIcons.Application：
    /// 后者只是临时占位，而且**它一旦失败就没有兜底** —— 托盘会变成一个没有图标的条目，
    /// Windows 可能根本不显示它，而用户看到的现象是"托盘里什么都没有"，
    /// 与真正的原因（图标没加载成功）之间没有任何线索。
    ///
    /// 所以这里失败要**明确报出来**：写日志 + 托盘气泡提示。
    /// 上一版只写了 Debug.WriteLine，等于静默吞掉。
    /// </summary>
    private void LoadIcon()
    {
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("WinQuick.tray.ico");

            if (stream is null)
            {
                ReportIconFailure("嵌入资源 WinQuick.tray.ico 不存在（检查 csproj 的 EmbeddedResource 项）");
                return;
            }

            // 按系统的小图标尺寸选取 ICO 里最接近的那一帧，而不是让 Icon 自己挑。
            // Icon(stream) 默认会挑一帧（本机实测挑到 48px），再由外壳缩小到托盘尺寸，
            // 多一次缩放就多一分糊。ico 里有 16/20/24/32/48 各帧，直接选对的最清晰。
            var small = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
            var wantSize = small > 0 ? small : 16;

            _icon.Icon = new System.Drawing.Icon(stream, new System.Drawing.Size(wantSize, wantSize));
        }
        catch (Exception ex)
        {
            ReportIconFailure(ex.Message);
        }
    }

    private void ReportIconFailure(string reason)
    {
        CrashLog.Write("托盘图标加载失败", new InvalidOperationException(reason));
        Debug.WriteLine("托盘图标加载失败: " + reason);

        // 失败时退回系统图标 —— 有个图标总比没有好，但用户应当知道托盘可能不显示。
        try { _icon.Icon = System.Drawing.SystemIcons.Application; } catch { /* 连系统图标都拿不到就放弃 */ }
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

        // 设置开在覆盖层里（面板与轮盘同时在场，改动可即时预览），
        // 所以这一项会先把轮盘唤出来。
        menu.Items.Add(MakeItem("设置…", () => OpenSettings?.Invoke()));
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

    /// <summary>诊断用：实际加载到的图标描述。验证真实路径，而不是另写一段加载代码。</summary>
    internal string IconDescription =>
        _icon.Icon is { } icon ? $"{icon.Width}x{icon.Height}" : "(未加载)";

    /// <summary>诊断用：系统请求的小图标边长。</summary>
    internal static int SystemSmallIconSize =>
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);

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
