using System.IO;

namespace WinQuick.Core.Config;

/// <summary>
/// 菜单项。M2 阶段只承载视觉所需的信息；M3 加入了动作定义。
/// </summary>
public sealed record MenuItemSpec
{
    public required string Label { get; init; }

    /// <summary>
    /// 图标来源（程序或文件的路径）。为 null 时界面上画首字占位图。
    /// </summary>
    public string? IconPath { get; init; }

    /// <summary>选中后要执行的动作。有子菜单的项为 null。</summary>
    public MenuAction? Action { get; init; }

    public IReadOnlyList<MenuItemSpec> Children { get; init; } = [];

    /// <summary>有子菜单的项会画一个朝向圆心外的指示标记。</summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>显式角度位置（0° = 正上）。为 null 时由 RadialLayout 均匀分布。</summary>
    public double? AngleDegrees { get; init; }
}

/// <summary>
/// 一份菜单定义。
/// </summary>
public sealed record MenuSpec(string Id, string Name, IReadOnlyList<MenuItemSpec> Items)
{
    /// <summary>
    /// 内置的默认菜单 —— M3 用它把动作执行打通，M4 会换成从 menus.json 读取。
    ///
    /// 路径会先探测是否存在，不存在就留空图标（界面退化为首字占位）。这个探测是必要的：
    /// 实测踩到的坑 —— Win11 上 <c>mspaint.exe</c>、<c>SystemSettings.exe</c> 这些文件
    /// 已经不存在（画图与设置都改成了商店应用），而 <c>explorer.exe</c> 也不在
    /// System32 下，在 %WINDIR% 根目录。硬编码路径会静默拿到空图标。
    ///
    /// 刻意混入了几条长标签（PowerShell、注册表编辑器、Windows 附件），
    /// 用来暴露文字过长被截断的问题。
    /// </summary>
    public static MenuSpec BuiltInDefault { get; } = new(
        Id: "default",
        Name: "主菜单",
        Items:
        [
            Item("资源管理器", ["explorer.exe"]),
            Item("命令提示符", ["System32", "cmd.exe"]),
            Item("记事本", ["System32", "notepad.exe"]),
            Item("PowerShell", ["System32", "WindowsPowerShell", "v1.0", "powershell.exe"]),
            Item("任务管理器", ["System32", "Taskmgr.exe"]),
            Item("注册表编辑器", ["regedit.exe"]),
            Item("字符映射表", ["System32", "charmap.exe"]),
            new MenuItemSpec
            {
                Label = "开发工具",
                // 有子项但图标留空：用首字占位，同时验证「子菜单指示标记」的绘制。
                Children =
                [
                    Item("命令提示符", ["System32", "cmd.exe"]),
                    Item("PowerShell", ["System32", "WindowsPowerShell", "v1.0", "powershell.exe"]),
                ],
            },
        ]);

    /// <summary>
    /// 按 %WINDIR% 下的相对路径构造菜单项，并据此生成启动动作。
    /// 文件不存在时图标留空 —— 界面上会画首字占位，比显示一个错误图标更诚实。
    /// </summary>
    private static MenuItemSpec Item(string label, string[] relativePath)
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var full = Path.Combine([windir, .. relativePath]);
        var exists = File.Exists(full) || Directory.Exists(full);

        return new MenuItemSpec
        {
            Label = label,
            IconPath = exists ? full : null,
            // 即使文件不存在也保留动作：让执行时报出"找不到目标"，
            // 比让菜单项完全不可点更容易排查。
            Action = MenuAction.Launch(full),
        };
    }
}
