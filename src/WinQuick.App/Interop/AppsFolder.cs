using System.Runtime.InteropServices;
using System.Text;

namespace WinQuick.App.Interop;

/// <summary>从 shell:AppsFolder 枚举出来的一个可启动项。</summary>
public sealed record ShellAppEntry(
    string DisplayName,
    string AppUserModelId,
    string ShellPath,
    bool IsUwpLike);

/// <summary>
/// 枚举 shell:AppsFolder —— 也就是「所有可以从开始菜单启动的东西」，包含
/// 传统桌面程序与 UWP/商店应用。
///
/// 为什么必须走这条路：Win11 上「设置」「计算器」「画图」「照片」这些
/// **没有可执行文件**（实测确认 System32\SystemSettings.exe 与 mspaint.exe 都不存在），
/// 既不能用文件路径取图标，也不能用文件路径启动。它们的唯一标识是
/// AppUserModelId，只能从 shell 的 AppsFolder 里枚举出来。
///
/// 实现上刻意**只用 IShellItem（5 个方法）**，不用 IShellItem2：
/// IShellItem2 的方法很多（GetPropertyStore/Update/GetProperty/GetBool/GetString…），
/// 而 COM 接口的方法顺序一旦写错，不会报错，只会静默读到垃圾数据甚至崩溃。
/// IShellItem 的 5 个方法顺序是确定的，风险低得多。
/// 取 AUMID 靠 GetDisplayName 的不同 SIGDN 取值 —— 实测确认哪个是它，见 AppsProbe。
/// </summary>
public static class AppsFolder
{
    // 已知文件夹与 shell 处理程序的 GUID
    private static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");

    // SIGDN 取值（shobjidl.h）
    public const uint SigdnNormalDisplay = 0x00000000;
    public const uint SigdnParentRelativeParsing = 0x80018001;
    public const uint SigdnDesktopAbsoluteParsing = 0x80028000;
    public const uint SigdnParentRelative = 0x80080001;

    private static IReadOnlyList<ShellAppEntry>? _cached;
    private static readonly object Gate = new();

    /// <summary>枚举结果（首次调用后缓存 —— 它基本不变，而枚举要遍历上百个 shell 项）。</summary>
    public static IReadOnlyList<ShellAppEntry> All()
    {
        lock (Gate)
        {
            return _cached ??= Enumerate();
        }
    }

    private static IReadOnlyList<ShellAppEntry> Enumerate()
    {
        var result = new List<ShellAppEntry>(256);

        var iid = IidShellItem;
        var hr = SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var folderObj);
        if (hr != 0 || folderObj is null) return result;

        try
        {
            var folder = (IShellItem)folderObj;
            var bhid = BhidEnumItems;
            var iidEnum = IidEnumShellItems;

            folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var enumObj);
            if (enumObj == IntPtr.Zero) return result;

            try
            {
                var enumerator = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumObj);

                while (true)
                {
                    enumerator.Next(1, out var item, out var fetched);
                    if (fetched == 0 || item is null) break;

                    var entry = Describe(item);
                    if (entry is not null) result.Add(entry);
                }

                Marshal.Release(enumObj);
            }
            finally
            {
                Marshal.ReleaseComObject(folder);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folderObj);
        }

        return result;
    }

    private static ShellAppEntry? Describe(IShellItem item)
    {
        var name = GetDisplayName(item, SigdnNormalDisplay);
        var parentRelative = GetDisplayName(item, SigdnParentRelative);
        var parentRelativeParsing = GetDisplayName(item, SigdnParentRelativeParsing);
        var desktopParsing = GetDisplayName(item, SigdnDesktopAbsoluteParsing);

        // AUMID 的样子是 "包族名!应用Id"，例如 Microsoft.WindowsCalculator_8wekyb3d8bbwe!App。
        // 不同 Windows 版本下它出现在哪个 SIGDN 里不完全一致，所以逐个找特征而不是写死一个。
        var aumid = FirstLooksLikeAumid(desktopParsing, parentRelativeParsing, parentRelative)
                    ?? "";

        if (string.IsNullOrWhiteSpace(name)) return null;

        return new ShellAppEntry(
            DisplayName: name,
            AppUserModelId: aumid,
            ShellPath: string.IsNullOrWhiteSpace(aumid) ? "" : $"shell:AppsFolder\\{aumid}",
            IsUwpLike: aumid.Contains('!'));
    }

    private static string? FirstLooksLikeAumid(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            // AUMID 的判定特征：含 '!' 分隔符，或含包族名常见的下划线+哈希段。
            if (candidate.Contains('!')) return candidate;
        }

        // 没有 '!' 的退一步用非空的那个 —— 传统桌面程序在 AppsFolder 里也会出现，
        // 它们的 AUMID 形式是完整文件路径。
        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;

        return null;
    }

    private static string GetDisplayName(IShellItem item, uint sigdn)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            item.GetDisplayName(sigdn, out ptr);
            if (ptr == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(ptr) ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>
    /// 按显示名（不区分大小写，部分匹配）找一个应用。
    /// 只适合探测用 —— 显示名随系统语言变化，不适合写进默认配置。
    /// </summary>
    public static ShellAppEntry? FindByName(string keyword)
    {
        foreach (var entry in All())
            if (entry.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    /// <summary>
    /// 按 AppUserModelId 前缀查找。
    ///
    /// 默认菜单靠它定位内置应用：**AUMID 与系统语言无关**，而显示名会随语言变化
    /// （中文系统上叫「计算器」，英文系统上叫「Calculator」）。
    /// 用 AUMID 查找、把显示名交给 shell 提供，才能在任何语言的系统上都成立。
    /// </summary>
    public static ShellAppEntry? FindByAumid(string prefix)
    {
        foreach (var entry in All())
            if (entry.AppUserModelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    // ---------------------------------------------------------------- COM

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        void Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }
}
