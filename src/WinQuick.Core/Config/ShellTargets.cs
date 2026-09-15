namespace WinQuick.Core.Config;

/// <summary>
/// 右键菜单里的一个"添加到…"目的地。
/// </summary>
/// <param name="Value">
/// 传给 <c>--add-to=</c> 的值。形如 <c>root</c> 或 <c>group:开发工具</c>。
///
/// 为什么用**分组名**而不是索引：索引会在增删项时整体移位，
/// 而注册表里的键可能停留在上一轮写入的状态 —— 那时点击会加进错误的分组，
/// 而且不会有任何报错。分组名对用户可读、也能在注册表里直接看懂，
/// 改名后查不到还能明确提示而不是猜。
/// </param>
/// <param name="Label">右键菜单里显示的文案。</param>
/// <param name="ParentIndex">对应的父项索引；-1 表示第一层。</param>
public readonly record struct ShellTarget(string Value, string Label, int ParentIndex);

/// <summary>
/// 右键菜单集成的纯逻辑：从菜单算出有哪些目的地、拼命令行、把参数解析回索引。
///
/// 单独放 Core 且全是纯函数，是因为这里出错的方式都很隐蔽且代价高：
/// 命令行引号拼错会让整条命令失效（点了没反应），参数解析错会把文件加到别的分组里。
/// 这些都必须能被单测逐条钉住，而不是靠人右键试出来。
/// </summary>
public static class ShellTargets
{
    /// <summary>根层目标的参数值。</summary>
    public const string RootValue = "root";

    /// <summary>分组目标的参数前缀。</summary>
    public const string GroupPrefix = "group:";

    /// <summary>
    /// 目的地数量上限。超过这个数右键菜单会长得难以阅读，
    /// 而右键菜单不是"能滚动"的界面 —— 超出的部分就直接不列出来。
    /// </summary>
    public const int MaxTargets = 24;

    /// <summary>
    /// 列出所有目的地：一级环，加上菜单里每个带子项的分组。
    ///
    /// 不含引号是硬性要求：分组名要嵌进命令行参数的引号里，
    /// 名字里再有一个引号就会把命令行截断 —— 那种命令点了不会有任何反应，
    /// 且很难从现象看出原因。这类名字直接跳过（调用方负责告知用户）。
    /// </summary>
    public static IReadOnlyList<ShellTarget> Build(MenuSpec menu)
    {
        var targets = new List<ShellTarget>
        {
            new(RootValue, "添加到一级环", -1),
        };

        for (var i = 0; i < menu.Items.Count; i++)
        {
            var item = menu.Items[i];
            if (!item.HasChildren) continue;
            if (item.Label.Contains('"')) continue;

            targets.Add(new ShellTarget(
                GroupPrefix + item.Label,
                $"添加到「{item.Label}」",
                i));
        }

        return targets.Count <= MaxTargets ? targets : targets.Take(MaxTargets).ToList();
    }

    /// <summary>被跳过的目的地（名字里含引号），供调用方提示用户。</summary>
    public static IReadOnlyList<string> Skipped(MenuSpec menu) =>
        menu.Items.Where(i => i.HasChildren && i.Label.Contains('"')).Select(i => i.Label).ToList();

    /// <summary>
    /// 拼出注册表里的命令行。
    ///
    /// <c>"%1"</c> 必须带引号：不带的话路径里有空格就会被 shell 拆成多个参数，
    /// 而那个错误在路径短的时候完全看不出来。exe 路径同理。
    /// </summary>
    public static string BuildCommand(string executablePath, ShellTarget target) =>
        $"\"{executablePath}\" --add-to=\"{target.Value}\" \"%1\"";

    /// <summary>
    /// 把 <c>--add-to</c> 的值解析成父项索引。
    /// 失败时返回 -1 并给出可以直接展示给用户的原因。
    /// </summary>
    public static bool TryResolve(MenuSpec menu, string? value, out int parentIndex, out string? error)
    {
        parentIndex = -1;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "右键菜单没有传目的地。";
            return false;
        }

        if (string.Equals(value, RootValue, StringComparison.Ordinal)) return true;

        if (!value.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            error = $"无法识别的目的地「{value}」。";
            return false;
        }

        var label = value[GroupPrefix.Length..];

        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i].HasChildren && string.Equals(menu.Items[i].Label, label, StringComparison.Ordinal))
            {
                parentIndex = i;
                return true;
            }
        }

        // 查不到就说清楚。分组改名/删除之后注册表会滞后一轮，
        // 这时"什么都不做"会让用户以为功能坏了。
        error = $"菜单里找不到分组「{label}」—— 它可能已被改名或删除。"
              + "右键菜单项会在下次改动菜单时自动刷新。";
        return false;
    }

    /// <summary>目的地的人类可读描述，用于托盘反馈。</summary>
    public static string Describe(MenuSpec menu, int parentIndex) =>
        parentIndex < 0 || parentIndex >= menu.Items.Count
            ? "一级环"
            : $"「{menu.Items[parentIndex].Label}」";
}
