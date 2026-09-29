namespace WinQuick.Core.Config;

/// <summary>
/// 一个菜单项被选中时要执行的动作。
/// </summary>
public sealed record MenuAction
{
    /// <summary>
    /// 动作类型：
    /// <list type="bullet">
    /// <item><c>launch</c> —— 启动程序（exe / lnk / 批处理），可带参数</item>
    /// <item><c>open</c> —— 用系统默认程序打开文件或文件夹</item>
    /// <item><c>url</c> —— 用默认浏览器打开网址</item>
    /// <item><c>shell</c> —— 交给资源管理器解释 shell 路径，UWP 应用走这条</item>
    /// </list>
    /// </summary>
    public string Type { get; init; } = "launch";

    public required string Target { get; init; }

    public string? Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>构造一个启动程序的动作。</summary>
    public static MenuAction Launch(string target, string? arguments = null) =>
        new() { Type = "launch", Target = target, Arguments = arguments };

    /// <summary>构造一个打开文件/文件夹的动作。</summary>
    public static MenuAction Open(string target) =>
        new() { Type = "open", Target = target };

    /// <summary>
    /// 构造一个打开**系统位置**的动作（回收站、控制面板、网络…这类虚拟文件夹）。
    ///
    /// 与 <see cref="Uwp"/> 是同一条执行路径（<c>explorer.exe &lt;target&gt;</c>）：
    /// 都没有可执行文件，只能交给资源管理器解释这个解析名。
    /// 之所以单独给个名字：调用方的意图不同（一个是应用，一个是位置），
    /// 而"用 UWP 的工厂去打开回收站"这种写法读起来是错的。
    /// </summary>
    public static MenuAction ShellLocation(string shellParsingName) =>
        new() { Type = "shell", Target = shellParsingName };

    /// <summary>
    /// 构造一个打开 UWP / 商店应用的动作。
    ///
    /// 为什么需要单独一条路径：Win11 上「设置」「计算器」「画图」「照片」这些
    /// 都没有可执行文件路径，只能用 AppUserModelId 通过 shell 启动。
    /// 实测确认过 <c>System32\SystemSettings.exe</c> 与 <c>mspaint.exe</c> 都已不存在。
    /// </summary>
    public static MenuAction Uwp(string appUserModelId) =>
        new() { Type = "shell", Target = appUserModelId };
}
