using System.IO;
using WinQuick.App.Actions;
using WinQuick.App.Diagnostics;
using WinQuick.Core.Config;

namespace WinQuick.App.M0;

/// <summary>
/// 动作执行探针。
///
/// 存在的理由：用户反馈「点击扇区没反应」。要区分清楚三种可能 ——
/// ①点击事件没被检测到 ②检测到了但没人执行 ③执行了但启动失败。
/// 覆盖层探针证明 ① 没问题，本探针证明 ③ 没问题，中间那条 ② 是当时真正缺的一环。
///
/// 做法：启动一个会自己退出、且留下可验证痕迹的命令（往临时文件写一行），
/// 然后核对文件内容。比"看进程列表里有没有 notepad"可靠得多，也不会留下残留进程。
/// </summary>
public static class ActionProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("action", "m3");
        ProbeOutput.Line("验证动作执行链路：动作模型 → ActionExecutor → Process.Start。");

        var results = new List<(string Name, bool Success, string Detail)>();
        var marker = Path.Combine(Path.GetTempPath(), $"winquick-action-probe-{Environment.ProcessId}.txt");

        try
        {
            var cmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");

            ProbeOutput.Section("shell 解释路径");
            ProbeOutput.Kv("cmd.exe 存在", File.Exists(cmd) ? "是" : "否");
            ProbeOutput.Kv("标记文件", marker);

            // ① launch：带参数启动，让被启动的进程留下可核对的痕迹
            if (File.Exists(marker)) File.Delete(marker);
            var launchItem = new MenuItemSpec
            {
                Label = "探针：写标记文件",
                Action = MenuAction.Launch(cmd, $"/c echo winquick-launch-ok > \"{marker}\""),
            };
            results.Add(await RunOneAsync(launchItem, marker, "winquick-launch-ok"));

            // ② open：用系统默认程序打开一个文件夹。
            //    这条不好自动核对（会真的弹出资源管理器窗口），所以只验证不抛异常。
            //    跳过 —— 弹窗会打扰用户，而它的代码路径与 ① 只差 action.Type 分支。
            ProbeOutput.Section("其余动作类型");
            ProbeOutput.Line("  open / url / shell 会真的弹出窗口或浏览器，为避免打扰不做自动触发。");
            ProbeOutput.Line("  它们与 launch 共用同一个 Process.Start 路径，只是入参不同。");

            // ③ 没有动作的项必须明确报错，不能静默无事发生
            ProbeOutput.Section("无动作的项");
            var noAction = new MenuItemSpec { Label = "有子菜单的项" };
            ActionResult? captured = null;
            void Handler(MenuItemSpec _, ActionResult r) => captured = r;
            ActionExecutor.Completed += Handler;
            ActionExecutor.Execute(noAction);

            for (var i = 0; i < 30 && captured is null; i++)
                await Task.Delay(100);
            ActionExecutor.Completed -= Handler;

            ProbeOutput.Kv("收到执行结果", captured is null ? "否（超时）" : "是");
            ProbeOutput.Kv("结果", captured?.Message ?? "(无)");
            ProbeOutput.Kv("是否明确报错", captured is { Success: false } ? "是（好：不会静默无事发生）" : "否");

            Report(results);
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("探针异常: " + ex);
            CrashLog.Write("action-probe", ex);
            Report(results);
        }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { /* 清理失败不影响结论 */ }
        }
    }

    private static async Task<(string, bool, string)> RunOneAsync(
        MenuItemSpec item, string markerPath, string expected)
    {
        ProbeOutput.Section($"动作类型 launch：「{item.Label}」");

        ActionResult? result = null;
        void Handler(MenuItemSpec _, ActionResult r) => result = r;
        ActionExecutor.Completed += Handler;

        var dispatched = DateTime.UtcNow;
        ActionExecutor.Execute(item);

        // 等回调，同时等文件出现 —— 回调只说明 Process.Start 没抛异常，
        // 文件里真的有内容才说明命令行参数被正确传给了子进程。
        var appeared = false;
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            if (File.Exists(markerPath) && new FileInfo(markerPath).Length > 0) { appeared = true; break; }
        }

        var elapsed = DateTime.UtcNow - dispatched;
        ActionExecutor.Completed -= Handler;

        var content = "";
        try { if (appeared) content = File.ReadAllText(markerPath).Trim(); }
        catch { /* 读不到就按内容不符处理 */ }

        ProbeOutput.Kv("执行回调", result is null ? "未收到" : (result.Success ? "成功" : "失败: " + result.Message));
        ProbeOutput.Kv("子进程留下标记文件", appeared ? "是" : "否");
        ProbeOutput.Kv("文件内容", content.Length > 0 ? content : "(空)");
        ProbeOutput.Kv("耗时", $"{elapsed.TotalMilliseconds:0} ms");

        var ok = appeared && content.Contains(expected, StringComparison.Ordinal);
        ProbeOutput.Line();
        ProbeOutput.Line(ok
            ? "  >>> 动作执行链路完整可用：Process.Start 成功，且参数被正确传递。"
            : "  >>> 动作未产生预期结果。");
        ProbeOutput.Line($"  注意：调用 Execute 是立即返回的，上面 {elapsed.TotalMilliseconds:0} ms 是" +
                         "等待子进程产出痕迹的时间，不是阻塞时间 —— 覆盖层的关闭路径不会被它拖住。");

        return (item.Label, ok, content);
    }

    private static void Report(List<(string Name, bool Success, string Detail)> results)
    {
        ProbeOutput.Section("汇总");
        foreach (var (name, ok, detail) in results)
            ProbeOutput.Kv(name, (ok ? "通过" : "失败") + (detail.Length > 0 ? $"  ({detail})" : ""));

        ProbeOutput.Kv("总体", results.Count > 0 && results.All(r => r.Success) ? "全部通过" : "存在失败");
        ProbeOutput.Flush();
    }
}
