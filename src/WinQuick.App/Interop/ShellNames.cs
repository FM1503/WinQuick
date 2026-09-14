using System.Diagnostics;
using System.IO;

namespace WinQuick.App.Interop;

/// <summary>
/// 取程序/文件的**本地化友好名**。
///
/// 数据来源是**可执行文件的版本资源 `FileDescription`** ——
/// 任务管理器「描述」列、开始菜单里显示的名字都是它。
/// 举几个实测值：notepad.exe → 「记事本」、explorer.exe → 「Windows 资源管理器」、
/// regedit.exe → 「注册表编辑器」。
///
/// 试过的两种更直觉的做法都不对：
/// - 文件名去掉扩展名 → 得到 "notepad"、"Taskmgr"，与 UWP 项的「计算器」「设置」
///   混在一起会显得一半中文一半英文文件名。
/// - `SHGetFileInfo(SHGFI_DISPLAYNAME)` → 它给的是**带扩展名的文件名**，
///   那只是资源管理器的"显示扩展名"开关在起作用，不是友好名。
///
/// 去掉 "Windows " / "Microsoft " 前缀：很多系统组件都以它开头，
/// 留着会让标签变长、在窄扇区里折得难看（"Windows 资源管理器" vs "资源管理器"）。
/// 这只是一个显示上的瘦身，不影响识别。
/// </summary>
public static class ShellNames
{
    private static readonly string[] NoisePrefixes =
    [
        "Windows® ", "Windows ", "Microsoft® ", "Microsoft ",
    ];

    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>取显示名。取不到就退回文件名去掉扩展名 —— 菜单里出现 "notepad" 也比空白好。</summary>
    public static string Get(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;

            var resolved = Resolve(path);
            Cache[path] = resolved;
            return resolved;
        }
    }

    private static string Resolve(string path)
    {
        var description = ReadDescription(path);
        if (string.IsNullOrWhiteSpace(description)) return Fallback(path);

        var trimmed = StripNoisePrefix(description.Trim());

        // 去掉前缀之后不该变成空的（理论上不会，但便宜的防御）。
        return string.IsNullOrWhiteSpace(trimmed) ? Fallback(path) : trimmed;
    }

    private static string? ReadDescription(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileDescription;
        }
        catch
        {
            // 有些 exe 的版本资源损坏或缺失，取不到就退回文件名。
            return null;
        }
    }

    private static string StripNoisePrefix(string text)
    {
        foreach (var prefix in NoisePrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var stripped = text[prefix.Length..].TrimStart();
            if (stripped.Length > 0) return stripped;
        }
        return text;
    }

    private static string Fallback(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}
