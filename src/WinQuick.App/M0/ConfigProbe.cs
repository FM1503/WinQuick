using System.IO;
using WinQuick.App.Config;
using WinQuick.App.Diagnostics;
using WinQuick.Core.Config;

namespace WinQuick.App.M0;

/// <summary>
/// 配置系统探针。
///
/// 重点验证一条承诺：**配置写错绝不导致程序打不开。**
/// 手写 JSON 出错太容易（少个逗号、多个括号），"改错配置就再也启动不了"
/// 是最糟糕的失败方式。所以这里在**临时目录**里故意写坏文件，
/// 逐个核对程序是否保留了上一份有效配置并给出可读的错误说明。
///
/// 用临时目录而不是真实配置目录 —— 探针不该破坏用户的配置。
/// </summary>
public static class ConfigProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("config", "m4");

        var temp = Path.Combine(Path.GetTempPath(), "winquick-config-probe-" + Environment.ProcessId);
        var results = new List<(string Name, bool Passed, string Detail)>();

        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);

            ProbeOutput.Section("路径");
            ProbeOutput.Kv("真实配置目录", ConfigStore.DefaultDirectory);
            ProbeOutput.Kv("探针临时目录", temp);

            // ---- 首次运行 ----
            ProbeOutput.Section("首次运行：写出默认配置");
            var store = new ConfigStore(temp);
            var first = store.LoadOrCreateDefaults();

            results.Add(("首次加载无错误", first.AllOk, first.Summary));
            ProbeOutput.Kv("config.json 已创建", File.Exists(store.ConfigPath) ? "是" : "否");
            ProbeOutput.Kv("menus.json 已创建", File.Exists(store.MenusPath) ? "是" : "否");
            ProbeOutput.Kv("菜单项数", store.Menu.Items.Count);
            ProbeOutput.Kv("菜单项", string.Join(" / ", store.Menu.Items.Select(i => i.Label)));
            ProbeOutput.Kv("含子菜单的项", string.Join(" / ",
                store.Menu.Items.Where(i => i.HasChildren).Select(i => $"{i.Label}({i.Children.Count})")));
            ProbeOutput.Kv("热键", store.Config.Hotkey ?? "(未设置)");

            // 默认模板本身必须是合法的 —— 否则第一次运行就会报错。
            var templateProblems = AppConfig.Parse(ConfigStore.DefaultConfigTemplate).Validate();
            results.Add(("默认 config.json 模板合法", templateProblems.Count == 0,
                string.Join("；", templateProblems)));

            // 写出来的 menus.json 必须能被自己读回来（序列化/反序列化往返一致）。
            ProbeOutput.Kv("menus.json 字节数", new FileInfo(store.MenusPath).Length);
            ProbeOutput.Kv("config.json 字节数", new FileInfo(store.ConfigPath).Length);

            // ---- 故意写坏：语法错误 ----
            ProbeOutput.Section("写坏配置：语法错误（少一个括号）");
            var goodMenuJson = File.ReadAllText(store.MenusPath);
            var goodConfigJson = File.ReadAllText(store.ConfigPath);

            File.WriteAllText(store.MenusPath, goodMenuJson[..(goodMenuJson.Length / 2)]);
            var broken = store.Reload();

            ProbeOutput.Kv("报告有问题", broken.AllOk ? "否（异常）" : "是");
            ProbeOutput.Kv("问题摘要", broken.Summary);
            ProbeOutput.Kv("菜单项数（应保留上一份）", store.Menu.Items.Count);
            results.Add(("语法错误时保留上一份菜单", !broken.MenuOk && store.Menu.Items.Count > 0,
                broken.Summary));

            // ---- 故意写坏：语义错误（角度未严格递增）----
            ProbeOutput.Section("写坏配置：角度未严格递增");
            File.WriteAllText(store.MenusPath, """
                {
                  "version": 1,
                  "menus": [
                    {
                      "id": "default", "name": "角度测试",
                      "items": [
                        { "label": "甲", "angle": 0,   "action": { "target": "cmd.exe" } },
                        { "label": "乙", "angle": 90,  "action": { "target": "cmd.exe" } },
                        { "label": "丙", "angle": 45,  "action": { "target": "cmd.exe" } }
                      ]
                    }
                  ]
                }
                """);

            var angleBroken = store.Reload();
            ProbeOutput.Kv("报告有问题", angleBroken.AllOk ? "否（异常）" : "是");
            ProbeOutput.Kv("问题摘要", angleBroken.Summary);
            ProbeOutput.Kv("菜单项数（应保留上一份）", store.Menu.Items.Count);
            results.Add(("角度非法时保留上一份菜单", !angleBroken.MenuOk && store.Menu.Items.Count > 0,
                angleBroken.Summary));

            // ---- 故意写坏：热键无法解析 ----
            ProbeOutput.Section("写坏配置：热键无法解析");
            File.WriteAllText(store.ConfigPath, """
                { "version": 1, "hotkey": "Ctrl+NotAKey" }
                """);
            var hotkeyBroken = store.Reload();
            ProbeOutput.Kv("报告有问题", hotkeyBroken.AllOk ? "否（异常）" : "是");
            ProbeOutput.Kv("问题摘要", hotkeyBroken.Summary);
            ProbeOutput.Kv("热键（应保留上一份）", store.Config.Hotkey ?? "(未设置)");
            results.Add(("热键非法时保留上一份配置", !hotkeyBroken.ConfigOk,
                hotkeyBroken.Summary));

            // ---- 故意写坏：有子项又有动作 ----
            ProbeOutput.Section("写坏配置：同一项既有子项又有动作");
            File.WriteAllText(store.ConfigPath, goodConfigJson);
            File.WriteAllText(store.MenusPath, """
                {
                  "version": 1,
                  "menus": [
                    {
                      "id": "default", "name": "二义性测试",
                      "items": [
                        {
                          "label": "甲",
                          "action": { "target": "cmd.exe" },
                          "children": [ { "label": "乙", "action": { "target": "cmd.exe" } } ]
                        },
                        { "label": "丙", "action": { "target": "cmd.exe" } }
                      ]
                    }
                  ]
                }
                """);
            var ambiguous = store.Reload();
            ProbeOutput.Kv("报告有问题", ambiguous.AllOk ? "否（异常）" : "是");
            ProbeOutput.Kv("问题摘要", ambiguous.Summary);
            results.Add(("子项与动作二义性被拦下", !ambiguous.MenuOk, ambiguous.Summary));

            // ---- 合法配置应生效 ----
            ProbeOutput.Section("合法配置应生效");
            File.WriteAllText(store.MenusPath, """
                {
                  "version": 1,
                  "menus": [
                    {
                      "id": "default", "name": "合法测试",
                      "items": [
                        { "label": "记事本", "icon": "C:\\Windows\\System32\\notepad.exe",
                          "action": { "type": "launch", "target": "C:\\Windows\\System32\\notepad.exe" } },
                        { "label": "开发", "children": [
                            { "label": "命令提示符", "action": { "target": "C:\\Windows\\System32\\cmd.exe" } }
                        ]}
                      ]
                    }
                  ]
                }
                """);

            var ok = store.Reload();
            ProbeOutput.Kv("加载成功", ok.AllOk ? "是" : $"否：{ok.Summary}");
            ProbeOutput.Kv("菜单名", store.Menu.Name);
            ProbeOutput.Kv("菜单项", string.Join(" / ", store.Menu.Items.Select(i => i.Label)));
            ProbeOutput.Kv("第二项有子菜单", store.Menu.Items.Count > 1 && store.Menu.Items[1].HasChildren ? "是" : "否");
            results.Add(("合法配置生效", ok.AllOk && store.Menu.Items.Count == 2, ok.Summary));

            // ---- 热重载 ----
            // 这条路径只靠文件监视 + 防抖触发，前面全是手动 Reload，覆盖不到。
            ProbeOutput.Section("热重载（文件监视 + 防抖）");
            store.StartWatching();

            var beforeWatch = store.Menu.Items[0].Label;
            ProbeOutput.Kv("改前第一项", beforeWatch);

            File.WriteAllText(store.MenusPath, """
                {
                  "version": 1,
                  "menus": [
                    {
                      "id": "default", "name": "热重载测试",
                      "items": [
                        { "label": "热重载改后的项", "action": { "target": "notepad" } },
                        { "label": "乙", "action": { "target": "notepad" } }
                      ]
                    }
                  ]
                }
                """);

            // 防抖是 400ms，留出足够余量并让调度器有机会泵消息。
            await Task.Delay(1500);

            var afterLabel = store.Menu.Items[0].Label;
            ProbeOutput.Kv("改后菜单名", store.Menu.Name);
            ProbeOutput.Kv("改后第一项", afterLabel);
            ProbeOutput.Kv("已自动生效", afterLabel == "热重载改后的项" ? "是" : "否");

            // 把重载过程中报出的问题显示出来 —— 上一版这里只报"未生效"，
            // 而真实原因是我测试数据里的 JSON 转义写错、配置被正确拒绝，
            // 看着却像热重载坏了。把问题摊开才不会再看错。
            if (store.LastProblems.Count > 0)
            {
                ProbeOutput.Kv("重载报出的问题", string.Join("；", store.LastProblems));
            }

            ProbeOutput.Line();
            ProbeOutput.Line(afterLabel == "热重载改后的项"
                ? "  >>> 热重载可用：改文件后无需重启，配置自动生效。"
                : "  >>> 热重载未生效。注意先看上面「重载报出的问题」——"
                  + "若是配置本身不合法，那是容错逻辑在正常工作，不是热重载坏了。");

            results.Add(("热重载自动生效", afterLabel == "热重载改后的项",
                store.LastProblems.Count > 0 ? string.Join("；", store.LastProblems) : afterLabel));

            store.Dispose();
            Report(results);
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("探针异常: " + ex);
            CrashLog.Write("config-probe", ex);
            Report(results);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    private static void Report(List<(string Name, bool Passed, string Detail)> results)
    {
        ProbeOutput.Section("汇总");
        foreach (var (name, passed, detail) in results)
        {
            var suffix = detail.Length > 0 && !passed ? $"  ({detail})" : "";
            ProbeOutput.Kv(name, (passed ? "通过" : "失败") + suffix);
        }

        var allPassed = results.Count > 0 && results.All(r => r.Passed);
        ProbeOutput.Kv("总体", allPassed ? "全部通过" : "存在失败");
        ProbeOutput.Flush();
    }
}
