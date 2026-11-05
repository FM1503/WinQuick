using System.Diagnostics;
using System.IO;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.Core.Config;

namespace WinQuick.App.Actions;

public sealed record ActionResult(bool Success, string Message)
{
    public static ActionResult Ok(string message) => new(true, message);
    public static ActionResult Fail(string message) => new(false, message);
}

/// <summary>
/// 动作执行。
///
/// 几条刻意的设计：
///
/// 1. **不阻塞调用方。** 启动进程可能耗时（UAC 提权、磁盘慢），而调用它的是
///    覆盖层的关闭路径。一旦在这里阻塞，用户按下扇区后界面就会卡住。
///    所以放到线程池上跑，失败通过回调报告。
///
/// 2. **执行顺序是「先收界面、再启动」。** 用户按下扇区后应当立刻看到界面消失、
///    焦点回到原窗口，然后新程序才出现。反过来的话，新程序窗口会先冒出来再被覆盖层挡住。
///
/// 3. **UWP / 商店应用走 shell 路径。** 实测确认 Win11 的「设置」「画图」都没有
///    可执行文件，只能用 AppUserModelId。见 <see cref="MenuAction.Uwp"/>。
/// </summary>
public static class ActionExecutor
{
    /// <summary>动作执行完成后的回调，用于把失败告诉用户。</summary>
    public static event Action<MenuItemSpec, ActionResult>? Completed;

    /// <summary>
    /// 执行一个菜单项的动作。立即返回，实际工作在后台线程完成。
    /// </summary>
    public static void Execute(MenuItemSpec item)
    {
        // 不阻塞：调用方（覆盖层关闭路径）必须立刻返回。
        ThreadPool.QueueUserWorkItem(_ => Report(item, ExecuteSync(item)));
    }

    /// <summary>
    /// 同步执行一个菜单项的动作，返回结果而**不**触发 <see cref="Completed"/>。
    ///
    /// 给启动队列用的：队列在后台线程上按自己的节奏逐项调用，结果由队列汇总成
    /// 一条提示。逐项走 <see cref="Execute"/> 的话，一次启动 10 个程序就会弹 10 次
    /// 「无法执行」气球，而用户要的信息只在汇总里（哪几项坏了、其余照常启动）。
    /// 失败项仍然写崩溃日志：不弹气球不等于不留痕，排查时日志是唯一线索。
    /// </summary>
    internal static ActionResult ExecuteSync(MenuItemSpec item)
    {
        var action = item.Action;
        if (action is null)
        {
            // 有子菜单的项被点击时不该走到这里；真走到了说明子菜单还没实现，
            // 应当明确说出来而不是静默无事发生 —— 后者会被当成程序坏了。
            return ActionResult.Fail($"「{item.Label}」还没有配置动作");
        }

        try
        {
            return Run(action);
        }
        catch (Exception ex)
        {
            CrashLog.Write($"执行动作失败: {item.Label}", ex);
            return ActionResult.Fail(ex.Message);
        }
    }

    private static ActionResult Run(MenuAction action)
    {
        if (string.IsNullOrWhiteSpace(action.Target))
            return ActionResult.Fail("动作没有指定目标");

        return action.Type switch
        {
            "launch" => StartProcess(action.Target, action.Arguments, action.WorkingDirectory),
            "open" => OpenWithShell(action.Target),
            "url" => OpenWithShell(NormalizeUrl(action.Target)),
            "shell" => RunViaExplorerShell(action.Target),
            _ => ActionResult.Fail($"未知的动作类型「{action.Type}」"),
        };
    }

    private static ActionResult StartProcess(string target, string? arguments, string? workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = target,
            // UseShellExecute=true 让系统处理 .lnk、文件关联、UAC 提权；
            // 设成 false 的话这些全都要自己实现，而且启动 .lnk 会直接失败。
            UseShellExecute = true,
        };

        if (!string.IsNullOrWhiteSpace(arguments)) info.Arguments = arguments;

        // 工作目录：开始菜单永远有它（快捷方式自带的「起始位置」），而我们直接存 exe
        // 路径时没有这个信息 —— 子进程会继承**我们**的目录。
        //
        // 这不是小事：不少程序（OBS、部分游戏与 IDE）要求从自己的目录启动，
        // 否则会报错或找不到资源。表现就是"外部双击正常、从工具里打开报错"。
        //
        // 对 .lnk / .url **不能**这样兜底：那会把快捷方式自己的「起始位置」覆盖成
        // 快捷方式所在的文件夹（比如桌面），反而更糟。那种情况交给 shell 解析。
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            info.WorkingDirectory = workingDirectory;
        }
        else if (!IsShellLink(target))
        {
            var ownDirectory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(ownDirectory) && Directory.Exists(ownDirectory))
                info.WorkingDirectory = ownDirectory;
        }

        // 相对路径按 %WINDIR% 之外的当前目录解析会失效，所以这里做一次存在性检查，
        // 给出比"系统找不到指定的文件"更有用的提示。
        if (looksLikePath(target) && !File.Exists(target) && !Directory.Exists(target))
            return ActionResult.Fail($"找不到目标：{target}");

        Process.Start(info);
        return ActionResult.Ok($"已启动 {Path.GetFileName(target)}");
    }

    /// <summary>
    /// 打开一批文件夹，尽可能放进**同一个资源管理器窗口的多个标签页**。
    ///
    /// 这里曾经是「把多个路径拼进一次 <c>explorer.exe</c> 调用的 Arguments」，
    /// 但实测（Win11 26300，三次）不成立：多参数调用只打开第一个路径，其余被忽略。
    /// 现在交给 <see cref="ExplorerTabs.OpenAll"/>，链路见那里的注释。
    ///
    /// 结果分两种：全部进了同一窗口的标签页，或者有一部分退化成独立窗口
    /// （UIA 的「添加新标签页」按钮不可用时的兜底 —— 退化成多窗口而不是整体失败）。
    /// </summary>
    internal static ActionResult ExecuteFoldersBatch(IReadOnlyList<MenuItemSpec> folders)
    {
        try
        {
            // 目标为空或为 null 的项不该出现在批里（调用方已过滤），真漏进来了也不能当成路径去开。
            var targets = folders
                .Select(f => f.Action?.Target)
                .OfType<string>()
                .ToList();
            // OfType 一步去掉 null 并把元素类型收窄成 string，
            // 免得 OpenAll 的签名被可空性拖成 IReadOnlyList<string?>。

            if (targets.Count == 0)
                return ActionResult.Fail("没有可打开的文件夹");

            var result = ExplorerTabs.OpenAll(targets);
            if (!result.Ok)
                return ActionResult.Fail(result.Error ?? "打开文件夹失败");

            if (result.SeparateWindows > 0)
            {
                return ActionResult.Ok(
                    $"已打开 {result.TabsOpened + result.SeparateWindows} 个文件夹：" +
                    $"{result.TabsOpened} 个同窗标签页，{result.SeparateWindows} 个独立窗口（标签页接口不可用）");
            }

            return ActionResult.Ok($"已在同一窗口打开 {result.TabsOpened} 个文件夹");
        }
        catch (Exception ex)
        {
            CrashLog.Write($"批量打开文件夹失败：{folders.Count} 个", ex);
            return ActionResult.Fail(ex.Message);
        }
    }

    /// <summary>是不是由 shell 解析的快捷方式（这类不能用"目标自己的目录"兜底）。</summary>
    private static bool IsShellLink(string target)
    {
        var ext = Path.GetExtension(target);
        return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private static ActionResult OpenWithShell(string target)
    {
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        return ActionResult.Ok($"已打开 {target}");
    }

    /// <summary>
    /// 交给资源管理器解释 <c>shell:AppsFolder\&lt;AppUserModelId&gt;</c>。
    ///
    /// 不能直接把 AppsFolder 路径交给 Process.Start —— 那不是文件路径，
    /// shell 只在 explorer 的解析上下文里认识它。
    /// </summary>
    private static ActionResult RunViaExplorerShell(string aumid)
    {
        var shellPath = aumid.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? aumid
            : "shell:AppsFolder\\" + aumid;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = shellPath,
            UseShellExecute = true,
        });
        return ActionResult.Ok($"已启动 {aumid}");
    }

    private static string NormalizeUrl(string url) =>
        url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;

    private static bool looksLikePath(string target) =>
        target.Contains('\\') || target.Contains('/') || target.Contains(':');

    private static void Report(MenuItemSpec item, ActionResult result)
    {
        if (!result.Success)
            CrashLog.Write($"动作执行失败: {item.Label}", new InvalidOperationException(result.Message));

        var handler = Completed;
        if (handler is null) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) handler(item, result);
        else dispatcher.BeginInvoke(() => handler(item, result));
    }
}
