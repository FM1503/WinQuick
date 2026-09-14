using System.Windows;
using Microsoft.Win32;

namespace WinQuick.App.Radial;

/// <summary>
/// 「选一个程序加进菜单」的文件对话框。
///
/// 单独抽成一个类型，是为了让"选程序的对话框长什么样"只有一处定义：
/// 标题、过滤器、以及"所有者是哪个窗口"这三件事必须始终一致。
/// 曾经设置面板里也有一份同样的对话框代码，两份的过滤器列表已经开始不一样了。
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
