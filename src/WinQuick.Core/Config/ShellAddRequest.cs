namespace WinQuick.Core.Config;

/// <summary>
/// 右键菜单点击后留下的一条"把某个路径加进某一级"的请求。
///
/// 由第二个进程写进配置目录，常驻实例取走并应用 —— **只有常驻实例会改 menus.json**。
/// 如果让第二个进程自己改配置文件，它就会和常驻实例的设置保存互相覆盖
/// （最后写入者赢，用户刚在设置面板里做的改动会被悄悄回退）。
/// </summary>
/// <param name="Value">目的地，取自 <see cref="ShellTargets"/>：<c>root</c> 或 <c>group:名称</c>。</param>
/// <param name="Path">被右键的那一项的完整路径。</param>
public sealed record ShellAddRequest(string Value, string Path);
