namespace WinQuick.Core.Config;

/// <summary>
/// 一个"系统位置"：显示名 + 交给谁去打开它。
///
/// <see cref="Target"/> 有两种形态，由 <see cref="IsShellNamespace"/> 区分：
/// · <c>shell:...</c> / <c>::{CLSID}</c> —— 虚拟文件夹（回收站、控制面板…），只能交给资源管理器解释；
/// · 真实路径 —— 用户目录下的已知文件夹与各驱动器（图片/文档/下载、C: 等）。
/// </summary>
public sealed record SystemLocation(string Label, string Target)
{
    /// <summary>是不是 shell 命名空间里的虚拟文件夹（没有文件系统路径）。</summary>
    public bool IsShellNamespace =>
        Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
        Target.StartsWith("::", StringComparison.Ordinal);

    /// <summary>打开它的动作：虚拟文件夹走 shell，真实路径走 open。</summary>
    public MenuAction ToAction() =>
        IsShellNamespace ? MenuAction.ShellLocation(Target) : MenuAction.Open(Target);
}

/// <summary>
/// 内置的"系统位置"清单（只有**虚拟文件夹**这一部分；已知文件夹与驱动器要在运行时取，
/// 见 App 侧的候选组装）。
///
/// ## 为什么只列这些名字（都是实测出来的，不是抄的）
///
/// 这份清单里每一条都在本机用产品取图标的那套 API
/// （<c>SHCreateItemFromParsingName</c> + <c>IShellItemImageFactory</c>）验过能解析出图标。
/// **没验过的名字一个都没放**，因为失败的样子很具体：下面这几个看似"应该能用"的
/// 别名在本机（Win11 26200）全部报 <c>DirectoryNotFoundException</c> ——
/// <c>shell:MyPictures</c>、<c>shell:MyMusic</c>、<c>shell:MyVideos</c>、<c>shell:UserProfile</c>。
/// 所以图片/音乐/视频/用户目录走**真实路径**（<c>Environment.GetFolderPath</c>），
/// 那样既是图标来源也是打开目标，不必和别名斗。
///
/// ## 运行期还要再筛一次
///
/// 不同 Windows 版本/不同机器上能解析的名字会有出入，所以 App 侧组装候选时
/// **逐个验证、解析不了的不显示**（见 SystemLocationPicker）——
/// 这份表是"候选"，不是"保证存在"。
/// </summary>
public static class SystemLocations
{
    /// <summary>虚拟文件夹候选项。顺序即界面顺序（常用的在前）。</summary>
    public static IReadOnlyList<SystemLocation> VirtualFolders { get; } =
    [
        new("回收站", "shell:RecycleBinFolder"),
        new("此电脑", "shell:MyComputerFolder"),
        new("桌面", "shell:Desktop"),
        new("网络", "shell:NetworkPlacesFolder"),
        new("控制面板", "shell:ControlPanelFolder"),
        new("最近使用", "shell:Recent"),
        new("字体", "shell:Fonts"),
        new("打印机", "shell:PrintersFolder"),
        new("Windows 目录", "shell:Windows"),
        new("程序目录", "shell:ProgramFiles"),
    ];
}
