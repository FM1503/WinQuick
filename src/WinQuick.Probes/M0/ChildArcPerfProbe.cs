using System.Diagnostics;
using WinQuick.App.Config;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.App.Interop;
using WinQuick.App.Radial;
using WinQuick.Core.Config;

namespace WinQuick.App.M0;

/// <summary>
/// 子环首展分段计时（spec smart-panel-placement-childarc-asyncload §2.2 / AC3）。
///
/// 回答两个问题：①冷缓存下「点开到 spinner 出现」的同步段是否 ≤300ms（此前约 1s 全阻塞）；
/// ②图标是否全部最终就绪（异步回调没丢）。
/// 对照组：热缓存（不清缓存再展一次）的同步段 —— 异步化不该让热路径变慢。
/// 结论必须能失败：任一指标越线即整体 fail。
///
/// 用法：--childarc-perf-probe
/// </summary>
public static class ChildArcPerfProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("childarc-perf", "m4");
        var results = new List<(string Name, bool Passed, string Detail)>();
        // F8：声明提到 try 外 —— finally 里收尾 Hide 才拿得到（异常段也要收尾，不留常驻窗口）。
        OverlayController? overlay = null, hot = null;

        try
        {
            var menu = new ConfigStore(ConfigStore.DefaultDirectory).Menu;
            var groupIndex = -1;
            MenuItemSpec? group = null;
            for (var i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i].HasChildren) { group = menu.Items[i]; groupIndex = i; break; }
            }
            if (group is null)
            {
                results.Add(("菜单里有分组项（否则没有子环可展）", false, "menus.json 无子菜单"));
                return;
            }

            overlay = new OverlayController(menu);

            ProbeOutput.Section("冷缓存首展");
            var cacheCleared = ShellIcons.ClearCachesForProbe();
            results.Add(("磁盘图标缓存确已清空", cacheCleared,
                cacheCleared ? "冷缓存段有效" : "删除失败（占用/权限）——冷缓存段无效"));
            var sw = Stopwatch.StartNew();
            overlay.Show();
            // 先让首帧与窗口模板 JIT 完成——计的必须是「展开子环」这一段，
            // Show 的成本属于唤出轮盘，不算在首展卡顿里（用户报障是点分组后的那段）。
            await Task.Delay(300);
            sw.Restart();
            overlay.ExpandChildArcForProbe(groupIndex);
            // 展开是同步的：返回即 spinner 已入树（或图标直接命中）。
            var coldSyncMs = sw.ElapsedMilliseconds;
            await Task.Delay(4000); // 给后台抽取足够时间（磁盘+COM，实测量级秒级）
            var coldTotalMs = sw.ElapsedMilliseconds;
            results.Add(("冷缓存同步段 ≤300ms", coldSyncMs <= 300, $"{coldSyncMs}ms（spinner/图标入树）"));
            var state = overlay.ChildArcLoadStateForProbe();
            results.Add(("图标全部就绪且 spinner 无残留",
                state.SpinnersAlive == 0 && state.IconsWithSource == state.TotalIcons && state.TotalIcons > 0,
                $"图标 {state.IconsWithSource}/{state.TotalIcons}，残留 spinner {state.SpinnersAlive}，等待 {coldTotalMs}ms"));

            ProbeOutput.Section("热缓存对照");
            hot = new OverlayController(menu);
            hot.Show();
            await Task.Delay(300);
            sw.Restart();
            hot.ExpandChildArcForProbe(groupIndex);
            var hotSyncMs = sw.ElapsedMilliseconds;
            results.Add(("热缓存同步段不劣于冷缓存", hotSyncMs <= Math.Max(coldSyncMs, 50),
                $"{hotSyncMs}ms vs 冷 {coldSyncMs}ms"));

        }
        catch (Exception ex)
        {
            results.Add(("探针异常", false, ex.ToString()));
        }
        finally
        {
            overlay?.Hide();
            hot?.Hide();
            ProbeOutput.Section("结论");
            var allPass = results.All(r => r.Passed);
            foreach (var (name, passed, detail) in results)
                ProbeOutput.Kv(name, (passed ? "PASS" : "FAIL") + "  " + detail);
            ProbeOutput.Line(allPass ? "全部通过" : "存在失败项");
            ProbeOutput.Flush();
        }
    }
}
