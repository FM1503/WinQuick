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
    /// <summary>
    /// 菜单语境（启动器）的过滤器：程序类放第一档，因为「加到菜单」绝大多数时候选的就是程序；
    /// 所有文件留作兜底（文档也能加进菜单，用户自己会切）。
    /// </summary>
    private const string ProgramFilter =
        "程序与快捷方式 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|所有文件 (*.*)|*.*";

    /// <summary>
    /// 队列语境的过滤器：所有文件放第一档（默认选中）。
    /// 队列是「把要打开的东西列进来」，文档与程序同等地位 —— 而默认档是程序时，
    /// 用户选 .docx 根本看不到（这就是"队列加不了文档"的根因）。
    /// </summary>
    private const string AllFilesFilter =
        "所有文件 (*.*)|*.*|程序与快捷方式 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd";

    /// <summary>
    /// 弹出选择对话框；用户取消时返回 null。
    ///
    /// <paramref name="owner"/> 必须是调用方自己的窗口。有所有者的对话框会跟着所有者一起
    /// 处于置顶之列，但**不会自动发生** —— 官方语义是「一个窗口*变成* topmost 时，
    /// 它拥有的窗口才连带置顶」，而调用方早就是 topmost 了。
    /// 所以对话框仍然需要外部周期性重申 z 序把它补上去，这就是两个回调存在的原因。
    /// </summary>
    /// <param name="allFilesFirst">
    /// 为 true 时「所有文件」放第一档（默认选中）、标题改成「选择要加入队列的文件」。
    /// 默认 false 保持菜单语境的行为不变 —— 两条入口刻意不同：菜单是启动器语境，保持程序类过滤。
    /// </param>
    public static string? Pick(
        Window owner,
        Action? modalOpening = null,
        Action? modalClosed = null,
        bool allFilesFirst = false)
    {
        var dialog = new OpenFileDialog
        {
            Title = allFilesFirst ? "选择要加入队列的文件" : "选择要加入菜单的程序",
            Filter = allFilesFirst ? AllFilesFilter : ProgramFilter,
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
