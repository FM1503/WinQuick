using System.Diagnostics;
using System.IO;
using WinQuick.App.Diagnostics;
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
        var action = item.Action;
        if (action is null)
        {
            // 有子菜单的项被点击时不该走到这里；真走到了说明子菜单还没实现，
            // 应当明确说出来而不是静默无事发生 —— 后者会被当成程序坏了。
            Report(item, ActionResult.Fail($"「{item.Label}」还没有配置动作"));
            return;
        }

        // 不阻塞：调用方（覆盖层关闭路径）必须立刻返回。
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Report(item, Run(action));
            }
            catch (Exception ex)
            {
                CrashLog.Write($"执行动作失败: {item.Label}", ex);
                Report(item, ActionResult.Fail(ex.Message));
            }
        });
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
        if (!string.IsNullOrWhiteSpace(workingDirectory)) info.WorkingDirectory = workingDirectory;

        // 相对路径按 %WINDIR% 之外的当前目录解析会失效，所以这里做一次存在性检查，
        // 给出比"系统找不到指定的文件"更有用的提示。
        if (looksLikePath(target) && !File.Exists(target) && !Directory.Exists(target))
            return ActionResult.Fail($"找不到目标：{target}");

        Process.Start(info);
        return ActionResult.Ok($"已启动 {Path.GetFileName(target)}");
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
