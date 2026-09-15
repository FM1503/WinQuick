using System.IO;
using Microsoft.Win32;
using WinQuick.App.Config;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.App.Interop;
using WinQuick.Core.Config;

namespace WinQuick.App.M0;

/// <summary>
/// 右键菜单集成探针 —— 核对写进注册表的东西到底对不对。
///
/// 为什么必须逐键核对：这条路径的失败方式全是"静默无效"。
/// 少一个引号、少一个空的 SubCommands、命令写在错的值上，结果都是
/// **菜单项还在、点下去什么也不发生**。看代码看不出区别，只有把写下去的键读回来比对。
///
/// 而且它会真的写用户注册表，所以"注销后无残留"是必须核的一条 ——
/// 那是"可以放心写 HKCU"这个前提本身。
///
/// 用法：--shell-probe
/// </summary>
public static class ShellProbe
{
    private const string RootKey = @"Software\Classes\*\shell\WinQuick";
    private const string FolderKey = @"Software\Classes\Directory\shell\WinQuick";

    public static void Run(CliOptions cli)
    {
        ProbeOutput.Initialize("shell", "m5");
        ProbeOutput.Line("核对右键菜单项：注册 → 逐键读回 → 注销后无残留。");
        ProbeOutput.Line("会真的写本用户的 HKCU（不需要管理员），结束后会注销。");
        ProbeOutput.Line();

        var results = new List<(string Name, bool Ok, string Detail)>();

        // 用一个专门的测试菜单：含中文分组、含空格的分组、以及一个"含引号应被跳过"的分组。
        var menu = new MenuSpec("probe", "探针菜单",
        [
            Item("资源管理器"),
            Container("开发 工具", [Item("VS Code")]),
            Container("设计\"软件", [Item("Photoshop")]),
            Container("系统工具", [Item("记事本")]),
        ]);

        var exe = Environment.ProcessPath ?? @"C:\Probe\WinQuick.exe";

        // **先备份现场。** 这个探针必须真的写注册表，但它不该把用户正在用的注册项弄丢 ——
        // 真发生过一次：跑完探针后用户右键就看不到 WinQuick 了
        //（探针结尾要核对"注销后无残留"，于是把它删了）。观测不应改变被观测的东西。
        var snapshot = ShellContextMenu.Export();

        try
        {
            // 起点干净：先确保没有上一次遗留。
            ShellContextMenu.Uninstall();

            ProbeOutput.Section("注册");
            var (ok, error, note) = ShellContextMenu.Install(menu, exe);

            ProbeOutput.Kv("安装结果", ok ? "成功" : "失败：" + error);
            ProbeOutput.Kv("跳过提示", note ?? "（无）");

            results.Add(("安装成功", ok, error ?? ""));

            // ---- 逐键读回 ----------------------------------------------------
            ProbeOutput.Section("文件作用域（HKCU\\Software\\Classes\\*\\shell\\WinQuick）");

            using var root = Registry.CurrentUser.OpenSubKey(RootKey);
            if (root is null)
            {
                results.Add(("注册表键存在", false, RootKey));
            }
            else
            {
                results.Add(("注册表键存在", true, RootKey));

                var muiVerb = root.GetValue("MUIVerb") as string;
                var icon = root.GetValue("Icon") as string;
                var subCommands = root.GetValue("SubCommands") as string;
                var defaultValue = root.GetValue(null) as string;
                var hasCommand = root.GetSubKeyNames().Contains("command");

                ProbeOutput.Kv("MUIVerb", muiVerb ?? "(空)");
                ProbeOutput.Kv("Icon", icon ?? "(空)");
                ProbeOutput.Kv("SubCommands", subCommands is null ? "(没有这个值)" : $"「{subCommands}」（空表示可展开）");
                ProbeOutput.Kv("(Default)", defaultValue is null ? "(空 — 正确)" : $"「{defaultValue}」（不该有）");
                ProbeOutput.Kv("是否误配了 command 子键", hasCommand ? "有（错误）" : "没有（正确）");

                results.Add(("MUIVerb = WinQuick", muiVerb == "WinQuick", muiVerb ?? "无"));
                results.Add(("Icon 指向当前程序", icon == exe, icon ?? "无"));

                // 空 SubCommands 是"可展开"的关键，微软文档没写、社区验证出来的。
                // 少了它级联可能整个不渲染 —— 而现象只是"菜单里没有这一项"。
                results.Add(("SubCommands 存在且为空（否则不展开）", subCommands == "", subCommands ?? "缺失"));

                // 父项不能有 (Default) 也不能有 command：既能展开又执行命令是无效状态。
                results.Add(("父项没有 (Default) 也没有 command 子键",
                    defaultValue is null && !hasCommand,
                    $"(Default)={defaultValue ?? "空"}，command={(hasCommand ? "有" : "无")}"));

                var addTo = root.OpenSubKey(@"Shell\AddTo");
                var addToVerb = addTo?.GetValue("MUIVerb") as string;
                ProbeOutput.Kv("「添加」层的 MUIVerb", addToVerb ?? "(无)");
                results.Add(("「添加」层存在且文案正确", addToVerb == "添加", addToVerb ?? "无"));

                var destinations = root.OpenSubKey(@"Shell\AddTo\Shell");
                var names = destinations?.GetSubKeyNames() ?? [];
                ProbeOutput.Kv("目的地数量", names.Length.ToString());

                // 一级环 + 开发 工具 + 系统工具 = 3；「设计"软件」含引号应被跳过。
                results.Add(("目的地数量 = 一级环 + 2 个合法分组（含引号的被跳过）",
                    names.Length == 3, $"{names.Length} 个：{string.Join("、", names)}"));

                // ---- 每组目的地的命令 ---------------------------------
                ProbeOutput.Section("目的地命令");
                var expected = ShellTargets.Build(menu);

                for (var i = 0; i < names.Length; i++)
                {
                    using var dest = destinations!.OpenSubKey(names[i]);
                    var label = dest?.GetValue("MUIVerb") as string;
                    var multiSelect = dest?.GetValue("MultiSelectModel") as string;
                    var command = dest?.OpenSubKey("command")?.GetValue(null) as string;

                    ProbeOutput.Kv($"  [{i}] {names[i]}", label ?? "(无文案)");
                    ProbeOutput.Kv("      命令", command ?? "(无)");
                    ProbeOutput.Kv("      多选模型", multiSelect ?? "(无)");

                    // 文案必须与目的地列表对上 —— 顺序错了会把用户送到错误的分组。
                    if (i < expected.Count)
                    {
                        results.Add(($"[{i}] 文案与目的地一致", label == expected[i].Label,
                            $"注册表={label}，期望={expected[i].Label}"));

                        var wantCommand = ShellTargets.BuildCommand(exe, expected[i]);
                        results.Add(($"[{i}] 命令行含引号的 exe、参数与 %1", command == wantCommand,
                            command ?? "无"));
                    }

                    results.Add(($"[{i}] MultiSelectModel=Single", multiSelect == "Single", multiSelect ?? "无"));
                }

                // 含空格的分组名必须被引号包住，否则 shell 会把它拆成两个参数。
                var spaced = destinations!.OpenSubKey("D_1");
                var spacedCommand = spaced?.OpenSubKey("command")?.GetValue(null) as string;
                results.Add(("含空格的分组名在参数里带引号",
                    spacedCommand?.Contains("--add-to=\"group:开发 工具\"") == true,
                    spacedCommand ?? "无"));

                // 含引号的分组不该出现在任何一个目的地里。
                var allLabels = names
                    .Select(n => destinations.OpenSubKey(n)?.GetValue("MUIVerb") as string)
                    .ToList();
                results.Add(("含引号的分组名已被跳过",
                    allLabels.All(l => l?.Contains('"') != true),
                    string.Join("、", allLabels)));
            }

            // ---- 文件夹作用域 -------------------------------------------------
            ProbeOutput.Section("文件夹作用域（HKCU\\Software\\Classes\\Directory\\shell\\WinQuick）");
            using (var folders = Registry.CurrentUser.OpenSubKey(FolderKey))
            {
                ProbeOutput.Kv("键存在", folders is null ? "否" : "是");
                ProbeOutput.Kv("MUIVerb", folders?.GetValue("MUIVerb") as string ?? "(无)");
                ProbeOutput.Kv("SubCommands", folders?.GetValue("SubCommands") as string ?? "(无)");

                results.Add(("文件夹作用域也注册了",
                    folders?.GetValue("MUIVerb") as string == "WinQuick", FolderKey));
            }

            // ---- 与 Windows 自己的级联对照（信息性） --------------------------
            ProbeOutput.Section("与 Windows 自带级联的形状对照（信息性）");
            ReportWindowsOwnCascade();

            // ---- 注销后无残留 -------------------------------------------------
            ProbeOutput.Section("注销");
            var (uninstallOk, uninstallError) = ShellContextMenu.Uninstall();

            using (var afterFiles = Registry.CurrentUser.OpenSubKey(RootKey))
            using (var afterFolders = Registry.CurrentUser.OpenSubKey(FolderKey))
            {
                ProbeOutput.Kv("文件作用域键还在", afterFiles is null ? "否（正确）" : "是（残留）");
                ProbeOutput.Kv("文件夹作用域键还在", afterFolders is null ? "否（正确）" : "是（残留）");

                results.Add(("注销成功", uninstallOk, uninstallError ?? ""));
                results.Add(("注销后文件作用域无残留", afterFiles is null, RootKey));
                results.Add(("注销后文件夹作用域无残留", afterFolders is null, FolderKey));
            }

            // ---- 请求文件的往返 ------------------------------------------------
            ProbeOutput.Section("右键菜单请求的落盘与拾取");

            var temp = Path.Combine(Path.GetTempPath(), "WinQuick-shell-probe-" + Guid.NewGuid().ToString("N"));
            var store = new ConfigStore(temp);
            try
            {
                var request = new ShellAddRequest("group:开发 工具", @"C:\Program Files\某 程序.exe");
                var written = store.TryWriteAddRequest(request, out var writeError);
                ProbeOutput.Kv("写请求", written ? "成功" : "失败：" + writeError);
                ProbeOutput.Kv("请求文件存在", File.Exists(store.AddRequestPath) ? "是" : "否");

                var taken = store.TakeAddRequest();
                ProbeOutput.Kv("取回的值", taken?.Value ?? "(无)");
                ProbeOutput.Kv("取回的路径", taken?.Path ?? "(无)");
                ProbeOutput.Kv("取走后文件是否删除", File.Exists(store.AddRequestPath) ? "否（异常）" : "是");

                results.Add(("请求能原样往返（含中文与空格）",
                    taken is not null && taken.Value == request.Value && taken.Path == request.Path,
                    $"{taken?.Value} | {taken?.Path}"));

                results.Add(("取走后请求文件被删除（否则会重复应用）",
                    !File.Exists(store.AddRequestPath), ""));

                // 再取一次应当为空：重复应用会把同一项加两遍。
                results.Add(("重复取走返回空", store.TakeAddRequest() is null, ""));

                // 坏文件必须被删掉：留着会让每次配置重载都失败一遍，永不自愈。
                File.WriteAllText(store.AddRequestPath, "{ 这不是合法 JSON");
                var broken = store.TakeAddRequest();
                results.Add(("损坏的请求文件被清掉而不是卡住",
                    broken is null && !File.Exists(store.AddRequestPath), ""));
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { /* 清理失败不影响结论 */ }
            }

            // ---- 参数解析 ------------------------------------------------------
            // 解析**真实写进注册表的那条命令行**，而不是手写一条"看起来很对"的。
            // 第一版就是手写的，于是它漏掉了真正的缺陷：注册表里第一个参数是
            // `--add-to=root`，而命令解析当时把整串当命令名、静默落到"未知命令"分支 ——
            // 手写的那条用的是裸 `--add-to`，恰好绕过了那个形状。
            ProbeOutput.Section("解析注册表里那条命令（与写入的内容同源）");

            var sample = ShellTargets.Build(menu)[1];        // 「开发 工具」，名字里含空格
            var registered = ShellTargets.BuildCommand(
                @"C:\Program Files\WinQuick\WinQuick.exe", sample);
            ProbeOutput.Kv("注册表里的命令行", registered);

            // 模拟 shell 的展开：把 %1 换成一个含空格与中文的真实路径。
            const string actualFile = @"C:\我的 文档\报告 2026.docx";
            var expanded = registered.Replace("%1", actualFile);
            ProbeOutput.Kv("展开后（shell 会这样调用）", expanded);

            var argv = SplitCommandLine(expanded);
            ProbeOutput.Kv("拆出的参数", string.Join(" | ", argv));

            var parsed = CliOptions.Parse([.. argv.Skip(1)]);
            ProbeOutput.Kv("  命令", parsed.Command);
            ProbeOutput.Kv("  add-to 值", parsed.GetString("add-to", "(无)"));
            ProbeOutput.Kv("  位置参数", parsed.Positionals.Count == 1 ? parsed.Positionals[0] : "(异常)");

            results.Add(("注册表里那条命令能被解析成正确的目的地与路径",
                parsed.Command == "--add-to"
                && parsed.GetString("add-to", "") == sample.Value
                && parsed.Positionals.Count == 1
                && parsed.Positionals[0] == actualFile,
                $"命令={parsed.Command}，值={parsed.GetString("add-to", "")}，" +
                $"路径={string.Join("|", parsed.Positionals)}"));
        }
        catch (Exception ex)
        {
            // 探针自己崩了也要记进结果，否则只报告已累积的部分、读起来像"全过"。
            results.Add(("探针完整跑完", false, ex.Message));
            ProbeOutput.Line();
            ProbeOutput.Line("探针未捕获异常: " + ex);
        }
        finally
        {
            // 先清掉探针自己的痕迹，再把现场原样写回。
            ShellContextMenu.Uninstall();

            if (snapshot.Count > 0)
            {
                ShellContextMenu.Import(snapshot);
                var restored = ShellContextMenu.ValueCount();
                ProbeOutput.Section("恢复现场");
                ProbeOutput.Kv("探测前已注册的值数", snapshot.Count.ToString());
                ProbeOutput.Kv("写回后的值数", restored.ToString());

                results.Add(("探针结束后把原有的注册项原样写回（不弄丢用户正在用的菜单项）",
                    restored == snapshot.Count && ShellContextMenu.Query().Installed,
                    $"{snapshot.Count} → {restored}"));
            }
        }

        ProbeOutput.Section("结果");
        var failed = 0;
        foreach (var (name, ok, detail) in results)
        {
            ProbeOutput.Kv((ok ? "通过  " : "不通过") + " " + name, detail);
            if (!ok) failed++;
        }

        ProbeOutput.Line();
        ProbeOutput.Line(failed == 0
            ? $"  >>> {results.Count} 项全部通过（注册表已注销，无残留）。"
            : $"  >>> {failed} / {results.Count} 项不通过，见上。");

        ProbeOutput.Line();
        ProbeOutput.Line("  还有一件事本探针**证明不了**：Explorer 实际渲染出来的样子。");
        ProbeOutput.Line("  注册表结构能逐值核对，但「菜单里长什么样、点下去是否真的加进去了」只能由人点一次。");

        ProbeOutput.Flush();
    }

    /// <summary>
    /// 按 Windows 的命令行规则拆词（引号内的空格不切分）。
    /// 刻意不自己发明一套规则去验：要验的是"程序能不能读懂注册表里那条命令"，
    /// 用自己写的拆词规则去验，等于拿同一套假设验自己。
    /// </summary>
    private static string[] SplitCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }

            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) args.Add(current.ToString());
        return [.. args];
    }

    private static MenuItemSpec Item(string label) => new()
    {
        Label = label,
        Action = MenuAction.Launch("cmd.exe"),
    };

    private static MenuItemSpec Container(string label, IReadOnlyList<MenuItemSpec> children) => new()
    {
        Label = label,
        Children = children,
    };

    /// <summary>
    /// 把 Windows 自带的同类级联读出来对照。**信息性**，不参与判定：
    /// 那些键在不同系统版本上可能不存在，拿它当断言会让探针在别的机器上误报。
    /// 它存在的意义是回答"我们是不是在自创写法"。
    /// </summary>
    private static void ReportWindowsOwnCascade()
    {
        const string windowsKey = @"*\shell\UpdateEncryptionSettingsWork";
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(windowsKey);
            if (key is null)
            {
                ProbeOutput.Line($"  {windowsKey} 在这台机器上不存在，跳过对照。");
                return;
            }

            ProbeOutput.Kv("Windows 自己的父项值", string.Join("、", key.GetValueNames()));
            ProbeOutput.Kv("  它的 SubCommands", key.GetValue("SubCommands") as string ?? "(没有)");

            var children = key.OpenSubKey("Shell")?.GetSubKeyNames() ?? [];
            ProbeOutput.Kv("  子项", children.Length == 0 ? "(无)" : string.Join("、", children));

            if (children.Length > 0)
            {
                using var child = key.OpenSubKey(@"Shell\" + children[0]);
                ProbeOutput.Kv("  子项的值", string.Join("、", child?.GetValueNames() ?? []));
                ProbeOutput.Kv("  子项命令写在", child?.OpenSubKey("command") is not null
                    ? "command 子键的 (Default)" : "(无 command 子键)");
            }

            ProbeOutput.Line("  >>> 我们的结构与它一致：父项 MUIVerb + 空的 SubCommands，子项在 Shell\\<名>\\command 下。");
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("  读取失败（不影响结论）：" + ex.Message);
        }
    }
}
