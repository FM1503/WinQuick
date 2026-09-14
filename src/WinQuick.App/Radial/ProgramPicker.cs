using System.Windows;
using Microsoft.Win32;

namespace WinQuick.App.Radial;

/// <summary>
/// 「选一个程序加进菜单」的文件对话框。
///
/// 单独抽出来是因为它有两个调用方：设置面板的「＋ 添加程序…」，以及环里的「＋」扇区。
/// 标题与过滤器列表必须完全一致 —— 分散在两处迟早会漂移成两套，用户看到两个不同的浏览器。
/// </summary>
internal static class ProgramPicker
{
    private const string Filter =
        "程序与快捷方式 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|所有文件 (*.*)|*.*";

    /// <summary>
    /// 弹出选择对话框；用户取消时返回 null。
    ///
    /// <paramref name="owner"/> 必须是调用方自己的窗口。有所有者的对话框会跟着所有者一起
    /// 处于置顶之列，但**不会自动发生** —— 官方语义是「一个窗口*变成* topmost 时，
    /// 它拥有的窗口才连带置顶」，而调用方早就是 topmost 了。
    /// 所以对话框仍然需要外部周期性重申 z 序把它补上去，这就是两个回调存在的原因。
    /// </summary>
    public static string? Pick(Window owner, Action? modalOpening = null, Action? modalClosed = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要加入菜单的程序",
            Filter = Filter,
            CheckFileExists = true,
            Multiselect = false,
        };

        modalOpening?.Invoke();
        try
        {
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
        finally
        {
            modalClosed?.Invoke();
        }
    }
}
