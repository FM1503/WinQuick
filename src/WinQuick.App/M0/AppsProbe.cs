using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;

namespace WinQuick.App.M0;

/// <summary>
/// shell:AppsFolder 枚举探针。
///
/// 存在的理由：AppUserModelId 到底出现在哪个 SIGDN 取值里，我没有可靠的一手依据 ——
/// 而 COM 接口的方法顺序一旦写错不会报错，只会静默读到垃圾数据。
/// 所以先把这个枚举的实际结果、以及各 SIGDN 的原始取值都打出来，
/// 用实测确定结论，而不是靠猜。
/// </summary>
public static class AppsProbe
{
    public static void Run(CliOptions cli)
    {
        ProbeOutput.Initialize("apps", "m4");

        var entries = AppsFolder.All();

        ProbeOutput.Section("枚举结果");
        ProbeOutput.Kv("shell:AppsFolder 条目数", entries.Count);
        ProbeOutput.Kv("其中带 AppUserModelId 的", entries.Count(e => e.AppUserModelId.Length > 0));
        ProbeOutput.Kv("其中形似 UWP（AUMID 含 '!'）", entries.Count(e => e.IsUwpLike));

        ProbeOutput.Section("前 25 条");
        ProbeOutput.Line($"  {"显示名",-34} {"AUMID",-58} UWP");
        ProbeOutput.Line("  " + new string('-', 104));
        foreach (var entry in entries.Take(25))
        {
            ProbeOutput.Line($"  {Truncate(entry.DisplayName, 32),-34} {Truncate(entry.AppUserModelId, 56),-58} " +
                             $"{(entry.IsUwpLike ? "Y" : ".")}");
        }

        ProbeOutput.Section("抽样的原始 SIGDN 取值（用于确认哪个是 AUMID）");
        var samples = new[] { "设置", "计算器", "记事本", "画图", "照片", "终端", "Edge", "记事" };
        var found = 0;
        foreach (var keyword in samples)
        {
            var entry = AppsFolder.FindByName(keyword);
            if (entry is null) continue;

            found++;
            ProbeOutput.Line($"  [{keyword}] 显示名=\"{entry.DisplayName}\"");
            ProbeOutput.Line($"      AUMID 推测 = \"{entry.AppUserModelId}\"");
            ProbeOutput.Line($"      shell 路径 = \"{entry.ShellPath}\"");
        }
        if (found == 0) ProbeOutput.Line("  （这些常见应用一个也没匹配到 —— 关键词可能不匹配本机语言）");

        ProbeOutput.Section("能否用 AUMID 取到图标");
        // 这一步很关键：图标提取走 SHCreateItemFromParsingName，
        // 而它是否能解析 "shell:AppsFolder\<AUMID>" 需要实测确认。
        foreach (var entry in entries.Where(e => e.IsUwpLike).Take(4))
        {
            var icon = ShellIcons.Get(entry.ShellPath);
            ProbeOutput.Kv(Truncate(entry.DisplayName, 20),
                icon is null ? "取不到图标" : $"取得 {icon.PixelWidth}x{icon.PixelHeight}");
        }

        ProbeOutput.Section("UWP 应用启动路径");
        ProbeOutput.Line("  走 explorer.exe shell:AppsFolder\\<AUMID>（见 MenuAction.Uwp）。");
        ProbeOutput.Line("  探针不实际启动 —— 那会弹出真的窗口打扰你。");

        ProbeOutput.Flush();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
