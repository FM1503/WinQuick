namespace WinQuick.Core.Config;

/// <summary>
/// 从菜单展开出来的一条可点候选，<see cref="DisplayLabel"/> 带层级路径。
/// </summary>
public sealed record MenuCatalogEntry(string DisplayLabel, MenuItemSpec Item);

/// <summary>
/// 把菜单压平成一份「可点项清单」。
///
/// 用途只有一个：给「添加到队列」的选择面板做来源清单 —— 队列里的项往往就是菜单里
/// 已经有的程序，加项时不该让用户再去文件系统里找一遍同样的路径。
///
/// 规则：
/// <list type="bullet">
/// <item>深度优先、按菜单顺序展开全部层级；标签是层级路径，用「 › 」连接
///   （例：「影音 › Audacity」），顶层项就是自己的名字。</item>
/// <item>跳过没有动作的项（分组行）：它们只是分类标签，点下去没有东西可执行。</item>
/// <item>跳过 <c>queue</c> 类型的项：队列套队列没有意义 —— 执行器把一个队列当普通项
///   启动时只会再触发一次同一套流程，用户要的是"把这条队列里的程序加进来"，
///   而那件事本身还得在队列编辑器里做。</item>
/// </list>
/// </summary>
public static class MenuCatalog
{
    /// <summary>层级标签的连接符。用「 › 」而不是「/」：路径分隔符会让人以为是文件系统路径。</summary>
    public const string PathSeparator = " › ";

    /// <summary>给选择面板用的扁平清单；空菜单返回空列表。</summary>
    public static IReadOnlyList<MenuCatalogEntry> FlattenForPicker(MenuSpec menu)
    {
        var result = new List<MenuCatalogEntry>();
        foreach (var item in menu.Items)
            Walk(item, prefix: null, result);

        return result;
    }

    private static void Walk(MenuItemSpec item, string? prefix, List<MenuCatalogEntry> result)
    {
        var label = prefix is null ? item.Label : prefix + PathSeparator + item.Label;

        // 判定依据是 Action 是否为空，而不是 Children.Count：配置允许「有子项也有动作」的项，
        // 那种项两边都应该出现（它自己可执行，也是一个层级）。
        if (item.Action is { } action && action.Type != "queue")
            result.Add(new MenuCatalogEntry(label, item));

        foreach (var child in item.Children)
            Walk(child, label, result);
    }
}
