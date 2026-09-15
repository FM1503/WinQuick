using Microsoft.Win32;
using WinQuick.App.Diagnostics;
using WinQuick.Core.Config;

namespace WinQuick.App.Interop;

/// <summary>
/// 文件/文件夹右键菜单里的「WinQuick → 添加 → 目的地」。
///
/// **只在旧版右键菜单里可见**（`显示更多选项` 或 `Shift+F10`）。这是 Win11 的限制，
/// 不是实现取舍：新版紧凑菜单只接受"打包应用 + 原生 COM DLL 实现 IExplorerCommand"，
/// 而且它只支持一层子菜单 —— 本功能要的两层（添加 → 目的地）在那里根本做不出来。
/// 详见 docs/M5-shell-menu.md 的调研记录。
///
/// 走注册表而不是 shell 扩展 DLL 的理由很实际：DLL 会被 Explorer 加载，
/// 它一崩就把资源管理器带崩；注册表方式是纯数据，Explorer 只是读一遍。
/// 代价是列表不能动态计算 —— 所以**菜单一变就整棵重写**。
///
/// 全程 HKCU：不需要管理员，也不需要改动"绿色单文件"的形态。
/// </summary>
internal static class ShellContextMenu
{
    private const string VerbKeyName = "WinQuick";
    private const string AddSubKeyName = "AddTo";
    private const string DestinationPrefix = "D_";

    /// <summary>
    /// 动词库的键名（相对 <c>HKEY_CLASSES_ROOT</c>）。
    ///
    /// 目的地放在这里、由 <c>ExtendedSubCommandsKey</c> 引用，而不是继续嵌一层
    /// <c>Shell\</c> 子键 —— 后者能工作，但第三级弹出面板会被画上一圈**老式 3D 白边框**。
    ///
    /// 判断依据是"同机制不同结果"：第一级到第二级用的也是链式级联，那一级的面板没有边框，
    /// 差别只在于目的地那一级是链式的**第二段**（WinQuick → Shell\AddTo → Shell\D_*）。
    /// 而微软文档对"ExtendedSubCommandsKey 指向的键里能否再嵌套级联"始终没有验证 ——
    /// 我原来的结构正好踩在这个未验证位置上。Windows 自己用的是 ExtendedSubCommandsKey
    ///（本机实测 EFS 那条级联就是），于是照它的写法来。
    /// </summary>
    private const string LibraryKeyName = "WinQuick.AddTo";

    private const string LibraryPath = @"Software\Classes\" + LibraryKeyName;

    /// <summary>文件与文件夹两个作用域。文件夹用 <c>Directory</c>（文件系统目录）。</summary>
    private static readonly string[] VerbScopes =
    [
        @"Software\Classes\*\shell\" + VerbKeyName,
        @"Software\Classes\Directory\shell\" + VerbKeyName,
    ];

    /// <summary>
    /// 我们拥有的所有键。注销、备份、还原都遍历它 ——
    /// 漏掉动词库就会留下一个孤儿键，探针的"无残留"核对会抓到。
    /// </summary>
    private static readonly string[] AllKeys = [.. VerbScopes, LibraryPath];

    /// <summary>注册项里的一条值。</summary>
    internal sealed record Entry(string SubKeyPath, string? ValueName, string Value);

    /// <summary>
    /// 安装/重建右键菜单项。
    /// 返回 Note 时表示有需要告知用户的情况（例如某些分组被跳过）。
    /// </summary>
    public static (bool Ok, string? Error, string? Note) Install(MenuSpec menu, string executablePath)
    {
        try
        {
            var targets = ShellTargets.Build(menu);
            var skipped = ShellTargets.Skipped(menu);

            // 整棵子树先删后建。不做增量更新：目的地会随菜单增删，
            // 增量逻辑要处理"上一轮有、这一轮没有"的键，出错就是残留一条
            // 点了没反应的菜单项 —— 那种缺陷用户只会认为是程序坏了。
            foreach (var key in AllKeys)
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

            // 动词库只写一份，两个作用域共用（微软文档明确支持这样做）。
            WriteLibrary(targets, executablePath);

            foreach (var scope in VerbScopes)
                WriteScope(scope, executablePath);

            var note = skipped.Count > 0
                ? $"以下分组名里有引号，无法写进右键菜单，已跳过：{string.Join("、", skipped)}"
                : null;

            return (true, null, note);
        }
        catch (Exception ex)
        {
            CrashLog.Write("安装右键菜单失败", ex);
            return (false, ex.Message, null);
        }
    }

    /// <summary>彻底注销，不留任何键。</summary>
    public static (bool Ok, string? Error) Uninstall()
    {
        try
        {
            foreach (var key in AllKeys)
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

            return (true, null);
        }
        catch (Exception ex)
        {
            CrashLog.Write("注销右键菜单失败", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 已注册的 exe 路径。<c>Installed</c> 为 false 表示没装。
    ///
    /// 用途：程序被移动或改名之后，旧键会指向一个不存在的 exe，
    /// 右键点下去毫无反应。启动时比对一次，不一致就重写。
    /// </summary>
    public static (bool Installed, string? RegisteredExecutable) Query()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(VerbScopes[0]);
            if (key?.GetValue("MUIVerb") is not string) return (false, null);
            return (true, key.GetValue("Icon") as string);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>注册表里实际写下去的目的地数量，供探针核对。</summary>
    internal static int DestinationCount()
    {
        using var library = Registry.CurrentUser.OpenSubKey(LibraryPath + @"\shell");
        return library?.GetSubKeyNames().Length ?? 0;
    }

    /// <summary>
    /// 把当前注册项导出成一份可以原样写回的快照。
    ///
    /// **给探针用。** 探针必须真的写一遍注册表才能核对结构，但它不该因此把
    /// 用户正在用的注册项弄丢 —— 那件事真发生过一次：跑完探针之后，
    /// 用户右键就再也看不到 WinQuick 了（探针结尾要核对"注销后无残留"，于是把它删了）。
    /// 观测不应改变被观测的东西，所以先备份、跑完再原样写回。
    /// </summary>
    internal static List<Entry> Export()
    {
        var entries = new List<Entry>();

        foreach (var scope in AllKeys)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(scope);
                if (key is null) continue;
                Collect(key, scope, entries);
            }
            catch
            {
                // 导出失败就当作"原本没有" —— 探针不该因为备份失败而拒绝运行。
            }
        }

        return entries;
    }

    private static void Collect(RegistryKey key, string path, List<Entry> entries)
    {
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name) is string value)
                entries.Add(new Entry(path, name.Length == 0 ? null : name, value));

        foreach (var child in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(child);
            if (sub is not null) Collect(sub, $@"{path}\{child}", entries);
        }
    }

    /// <summary>把 <see cref="Export"/> 的快照原样写回。</summary>
    internal static void Import(IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) return;

        foreach (var key in AllKeys)
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

        foreach (var entry in entries)
        {
            using var key = Registry.CurrentUser.CreateSubKey(entry.SubKeyPath, writable: true);
            key?.SetValue(entry.ValueName, entry.Value, RegistryValueKind.String);
        }
    }

    /// <summary>当前注册项里的值总数（供探针核对备份/还原是否完整）。</summary>
    internal static int ValueCount() => Export().Count;

    /// <summary>
    /// 写第一、二级：<c>WinQuick</c> 下挂一个 <c>Shell\AddTo</c>，
    /// 后者用 <c>ExtendedSubCommandsKey</c> 指向动词库。
    /// </summary>
    private static void WriteScope(string scope, string executablePath)
    {
        using (var verb = Registry.CurrentUser.CreateSubKey(scope, writable: true)
                          ?? throw new InvalidOperationException($"无法创建注册表键 {scope}"))
        {
            verb.SetValue("MUIVerb", VerbKeyName, RegistryValueKind.String);
            verb.SetValue("Icon", executablePath, RegistryValueKind.String);

            // 空 SubCommands 才是"可展开"。这是社区验证出来的行为（微软文档没写）：
            // 少了它级联可能不渲染；同时也**不能**给这个键配 command 子键 ——
            // 一个既能展开又要执行命令的父项在 shell 里是无效状态。
            verb.SetValue("SubCommands", "", RegistryValueKind.String);

            // 刻意不写 (Default)：父项的默认值必须为空，否则 Explorer 会拿它当命令。
        }

        var addToPath = $@"{scope}\Shell\{AddSubKeyName}";
        using var addTo = Registry.CurrentUser.CreateSubKey(addToPath, writable: true)
                          ?? throw new InvalidOperationException($"无法创建注册表键 {addToPath}");

        addTo.SetValue("MUIVerb", "添加", RegistryValueKind.String);

        // 引用动词库。**不写 SubCommands** —— 这是 Windows 自己那条级联的写法
        //（本机实测 HKCR\*\shell\UpdateEncryptionSettingsWork 只有
        // MUIVerb + ExtendedSubCommandsKey，没有 SubCommands）。
        addTo.SetValue("ExtendedSubCommandsKey", LibraryKeyName, RegistryValueKind.String);
    }

    /// <summary>写动词库（目的地列表）。两个作用域共用这一份。</summary>
    private static void WriteLibrary(IReadOnlyList<ShellTarget> targets, string executablePath)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var path = $@"{LibraryPath}\shell\{DestinationPrefix}{i}";

            using var destination = Registry.CurrentUser.CreateSubKey(path, writable: true)
                                    ?? throw new InvalidOperationException($"无法创建注册表键 {path}");

            // 键名是 ASCII 序号、文案在 MUIVerb 里：分组改名时只改文案，
            // 键名不动，也就不必在注册表里做"删旧建新"的迁移。
            destination.SetValue("MUIVerb", target.Label, RegistryValueKind.String);

            // **每个目的地都要有图标。** 判据来自对照：本机两个"已知良好"的级联叶子
            //（Windows 自带的 EFS Decrypt、VS Code）都写了 Icon。图标借用程序自身的 ——
            // 分组本身没有更贴切的图可用，与第一级一致也更整齐。
            destination.SetValue("Icon", executablePath, RegistryValueKind.String);

            // 多选时隐藏这一项。旧版动词默认会"每个选中文件起一个进程"，
            // 而本功能一次只处理一个路径 —— 与其让用户以为会全部添加，
            // 不如多选时就不显示。
            destination.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);

            using var command = destination.CreateSubKey("command", writable: true)
                                ?? throw new InvalidOperationException($"无法创建注册表键 {path} 的 command");

            // 命令写在 (Default) 里，这是 shell 读取的位置。
            command.SetValue(null, ShellTargets.BuildCommand(executablePath, target), RegistryValueKind.String);
        }
    }

    // 这里刻意**没有**调 SHChangeNotify(SHCNE_ASSOCCHANGED)。
    //
    // 它是"改完关联通知一下 shell"的常规做法，但对静态动词没什么用：
    // Explorer 每次构建右键菜单时都会重新读一遍类存储，所以下一次右键就生效。
    // 而它的可靠性本身在社区里说法不一（有大量"调了没用"的报告），
    // 而这次调用的代价不小 —— CsWin32 生成的签名带 void* 参数，
    // 要调它就得给整个项目打开 unsafe。为一个效果存疑的调用打开 unsafe 不划算。
    //
    // 万一 Explorer 确实缓存了（社区里偶有报告），设置面板里给了
    // "重启资源管理器"的说明 —— 而不是在这里加一个不知道有没有用的调用。
}
