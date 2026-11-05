using System.Runtime.InteropServices;
using WinQuick.App.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace WinQuick.App.Interop;

/// <summary>
/// 选一个**任意文件夹**（「添加到菜单」里的「浏览文件夹…」）。
///
/// ## 为什么用 IFileDialog + FOS_PICKFOLDERS，而不是别的几条路
///
/// 1. **不用 WinForms 的 FolderBrowserDialog**：那要把整个 WinForms 依赖拖进一个 WPF 常驻程序
///    （还得多一个 <c>UseWindowsForms</c> 开关、和 WPF 的消息循环叠在一起）。更关键的是它默认
///    还是那个老式的树形对话框，观感与系统其它选择框对不上。
/// 2. **不用 OpenFileDialog 的 <c>ValidateNames = false</c> 技巧**（选个"文件"再取其目录）：
///    那是在文件框上硬拗出文件夹语义 —— 按钮文案还是「打开」、选中目录的行为随系统版本漂移，
///    用户看到的与我们要的并不是一回事。
/// 3. <c>IFileDialog</c> 是 Vista 起的公开 COM 接口，设一个选项位就得到系统原生的文件夹选择框，
///    与资源管理器里「选择文件夹」完全一致；不需要额外依赖，也不随注册表设置漂移。
///
/// ## 失败绝不外抛
///
/// 这个入口只是"添加一项"的一条支路，选文件夹失败（COM 建不出来、shell 拒绝、用户取消）都
/// 不该带崩覆盖层或菜单 —— 所以全程 try/catch，异常进崩溃日志后返回 null。
/// 调用方对 null 的唯一约定是「什么都没选」。
/// </summary>
internal static class FolderPicker
{
    /// <summary>
    /// 弹系统文件夹选择框；用户取消或任何失败都返回 null。
    ///
    /// <paramref name="ownerHwnd"/> 是对话框的属主窗口：有属主的选择框才跟着我们
    /// （轮盘/选择器）一起处于置顶之列，且焦点变化时不落到"别人的窗口"上。
    /// 传 0 时退回无属主（保留这条是为了探针/无窗口场景不被迫造一个句柄）。
    /// </summary>
    public static string? PickFolder(nint ownerHwnd)
    {
        IFileDialog? dialog = null;
        IShellItem? item = null;

        try
        {
            dialog = (IFileDialog)new FileOpenDialog();

            // FORCEFILESYSTEM：结果必须是文件系统路径（虚拟文件夹如「此电脑」选不了）——
            // 菜单里的 open 目标必须是真实路径，executor 与队列都按路径处理。
            // CsWin32 把 C 枚举 FOS_* 投影成了 FILEOPENDIALOGOPTIONS（枚举常量名不变）。
            dialog.SetOptions(
                FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS
                | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM);

            // 这里曾经还加过一个 0x00080000（以为是用来看路径输入框的 PATHBROWSER 位），
            // 结果整个入口"点了没反应"：FILEOPENDIALOGOPTIONS 里根本没有这个位，
            // SetOptions 对未定义位直接抛 E_INVALIDARG，于是每次都在这里失败、
            // 被下面的 catch 吞掉返回 null（2026-11-05 崩溃日志里两条「选择文件夹失败」为证）。
            // 「直接输入/粘贴一个深层路径」的需求由选择框自带的地址栏就能满足，
            // 不需要（FOS 也没有）额外的 PATHBROWSER 位 —— 别再按非官方资料加位。

            // 标题用通用的「选择要添加的文件夹」：这个框同时服务队列编辑器与轮盘菜单，
            // 两处都说"添加到菜单"会让队列那边看起来放错了地方。
            dialog.SetTitle("选择要添加的文件夹");

            // CsWin32 生成的 Show 是 retval HRESULT：取消与失败都以异常形式抛出（这里统一 catch），
            // 正常返回即表示用户按了「选择文件夹」。没有结果位表示取消，不必再区分。
            // 属主窗口用 HWND 投影：0 时传 default（无属主），否则弹出框挂在我们窗口下。
            unsafe
            {
                dialog.Show(ownerHwnd == 0 ? default : new HWND(ownerHwnd));
            }

            // 这两个出参都是原生指针（PWSTR / 接口指针），CsWin32 把它们放在 unsafe 签名里；
            // 调用点也不能在 unsafe 上下文外面。
            string? result = null;
            unsafe
            {
                dialog.GetResult(out item);
                if (item is not null)
                {
                    item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var rawPath);
                    if (rawPath.Value is not null)
                    {
                        result = rawPath.ToString();

                        // shell 分配的字符串由我们负责释放（文档要求 CoTaskMemFree）；
                        // 释放也要在 unsafe 块里，PWSTR 的指针只能在这里取。
                        Marshal.FreeCoTaskMem((nint)rawPath.Value);
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            CrashLog.Write("选择文件夹失败", ex);
            return null;
        }
        finally
        {
            // 每个 COM 对象各自释放：某一个释放失败不该连累另一个，
            // 也不该掩盖上面真正的错误（所以彼此独立 try/catch）。
            if (item is not null) ReleaseQuietly(item);
            if (dialog is not null) ReleaseQuietly(dialog);
        }
    }

    private static void ReleaseQuietly(object comObject)
    {
        try
        {
            // FinalReleaseComObject 而不是 ReleaseComObject：每次选择都是一次性对象，
            // 不残留 RCW 包装引用（常驻程序里这类短命 COM 对象会一次次累积）。
            Marshal.FinalReleaseComObject(comObject);
        }
        catch
        {
            // 已经释放或包装已坏都不值得再报；不影响返回值。
        }
    }
}
