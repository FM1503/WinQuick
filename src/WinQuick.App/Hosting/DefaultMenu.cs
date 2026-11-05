using System.IO;
using WinQuick.App.Interop;
using WinQuick.Core.Config;

namespace WinQuick.App.Hosting;

/// <summary>
/// 默认菜单的装配。
///
/// 为什么放在 App 层而不是 Core：它需要调用 shell 去枚举 AppsFolder（COM 互操作），
/// 而 Core 要保持纯逻辑、不依赖 Win32。Core 只定义菜单与动作的模型。
///
/// 装配策略：
/// 1. **文件型应用** —— 逐个探测候选路径是否存在，不存在就留空图标。
///    实测踩过的坑：Win11 上 mspaint.exe、SystemSettings.exe 都不存在，
///    而 explorer.exe 在 %WINDIR% 根目录而不是 System32。
/// 2. **UWP / 商店应用** —— 按 **AUMID 前缀**查找，而不是按显示名。
///    AUMID 与系统语言无关（`Microsoft.WindowsCalculator_8wekyb3d8bbwe!App` 在
///    任何语言下都一样），而显示名会变。显示名交给 shell 提供，自动本地化。
///    找不到就跳过该项 —— 用户没装某个商店应用时菜单不该出现死项。
/// 3. **启动队列**只放在「开发工具」子菜单里，不占顶层：顶层 8 项的钟面结构是 CHI'93 的
///    错误率拐点（见 <see cref="Build"/> 的注释），多塞一项就破坏「上/右上/右/…」的直觉。
///    已经生成过 menus.json 的用户不会自动拿到这一项（菜单是写进文件的），
///    文档里给了一段手写样例。
/// </summary>
public static class DefaultMenu
{
    /// <summary>
    /// 构造默认菜单。
    ///
    /// **顶层固定 8 项**：CHI'93 的实测结论是 8 扇区是错误率拐点，Kando 的官方经验值
    /// 也是 8、并且绝不超过 12。8 的另一个好处是「上/右上/右/…」的钟面直觉成立，
    /// 而奇数个扇区会失去这种直觉 —— 每项的角度都变得不好记。
    /// 多出来的项放进子菜单，顺便把两级结构用起来。
    /// </summary>
    public static MenuSpec Build()
    {
        var items = new List<MenuItemSpec>();

        AddFileItem(items, ["explorer.exe"]);
        AddUwpItem(items, "Microsoft.WindowsTerminal_", fallbackLabel: "终端");
        AddFileItem(items, ["System32", "notepad.exe"]);
        AddUwpItem(items, "Microsoft.WindowsCalculator_", fallbackLabel: "计算器");
        AddUwpItem(items, "windows.immersivecontrolpanel_", fallbackLabel: "设置");
        AddUwpItem(items, "Microsoft.Paint_", fallbackLabel: "画图");
        AddFileItem(items, ["System32", "Taskmgr.exe"]);

        // 第八项带子菜单，正好演示两级结构。
        var tools = new List<MenuItemSpec>();
        AddFileItem(tools, ["System32", "cmd.exe"]);
        AddUwpItem(tools, "Microsoft.WindowsTerminal_", fallbackLabel: "终端");
        AddFileItem(tools, ["regedit.exe"]);
        AddFileItem(tools, ["System32", "charmap.exe"]);

        // 启动队列：Target 留空 = "第一套"。不设 IconPath，走首字占位 ——
        // 这里没有实体文件能取图标，而给个错图标比首字更误导。
        tools.Add(new MenuItemSpec
        {
            Label = "启动队列",
            Action = new MenuAction { Type = "queue", Target = "" },
        });

        if (tools.Count > 0)
        {
            items.Add(new MenuItemSpec { Label = "开发工具", Children = tools });
        }

        return new MenuSpec("default", "主菜单", items);
    }

    private static void AddFileItem(List<MenuItemSpec> items, string[] relativePath)
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var full = Path.Combine([windir, .. relativePath]);

        var exists = File.Exists(full) || Directory.Exists(full);

        items.Add(new MenuItemSpec
        {
            // 显示名向 shell 要，而不是用文件名。
            // 文件名会得到 "notepad"、"Taskmgr" 这种，而资源管理器里显示的是
            // 「记事本」「任务管理器」；UWP 项的显示名本来就来自 shell，
            // 两者来源统一才不会出现"一半中文一半英文文件名"的菜单。
            // 同时这也让名字自动跟随系统语言，代码里不必硬编码任何标签。
            Label = exists ? ShellNames.Get(full) : Path.GetFileNameWithoutExtension(relativePath[^1]),

            // 图标路径与动作目标分开：UWP 项的图标走 shell 路径，文件项这里两者相同。
            IconPath = exists ? full : null,
            Action = MenuAction.Launch(full),
        });
    }

    private static void AddUwpItem(List<MenuItemSpec> items, string aumidPrefix, string fallbackLabel)
    {
        var entry = AppsFolder.FindByAumid(aumidPrefix);

        // 没装这个应用就跳过 —— 菜单里出现点不动的死项比少一项更糟。
        if (entry is null || !entry.IsUwpLike) return;

        items.Add(new MenuItemSpec
        {
            // 显示名用 shell 提供的那个，自动跟随系统语言。
            Label = string.IsNullOrWhiteSpace(entry.DisplayName) ? fallbackLabel : entry.DisplayName,
            // 图标必须走 shell 路径：UWP 应用没有可执行文件可供提取。
            IconPath = entry.ShellPath,
            Action = MenuAction.Uwp(entry.AppUserModelId),
        });
    }
}
