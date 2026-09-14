using WinQuick.Core.Geometry;

namespace WinQuick.Core.Config;

/// <summary>
/// 菜单的增删改。
///
/// 单独放在 Core 里而不是散在界面代码中，有两个实际好处：
/// 一是它是纯函数、可单测 —— 索引移位、"不能删到空"、"满了不许再加"这类规则
/// 全是可以被数值验证的，不该靠人点界面确认；
/// 二是现在有两条编辑入口（环里的「＋」扇区、右键扇区菜单），
/// 规则写在两处迟早会分叉 —— 比如一边允许删到空、另一边不允许。
///
/// 所有方法都不修改传入的 <see cref="MenuSpec"/>，返回新实例；被拒绝时原样返回，
/// 并通过 <c>Error</c> 说明原因。这样调用方不必自己先做一遍校验。
/// </summary>
public static class MenuEditor
{
    /// <summary>第一层最多几项。与扇区数硬上限一致 —— 再多就细到点不中。</summary>
    public const int MaxItems = RadialLayout.MaxSectors;

    /// <summary>
    /// 子层最多几项。比第一层宽松：子环可以撑满整圈，而第一层要留住
    /// 「上/右上/右/…」的钟面直觉，项数一多那种直觉就没了。
    /// </summary>
    public const int MaxChildItems = RadialLayout.MaxChildSectors;

    /// <summary>
    /// 往某一级加一项。<paramref name="parentIndex"/> 为 -1 表示加进第一层，
    /// 否则加进该父项的子项列表。
    /// </summary>
    public static (MenuSpec Menu, string? Error) Add(MenuSpec menu, int parentIndex, MenuItemSpec item)
    {
        if (parentIndex < 0)
        {
            if (menu.Items.Count >= MaxItems)
                return (menu, $"第一层最多 {MaxItems} 项，已经满了。");

            return (menu with { Items = menu.Items.Append(item).ToList() }, null);
        }

        if (parentIndex >= menu.Items.Count)
            return (menu, $"没有第 {parentIndex + 1} 项，无法往它里面加。");

        var parent = menu.Items[parentIndex];
        if (parent.Children.Count >= MaxChildItems)
            return (menu, $"「{parent.Label}」的子菜单最多 {MaxChildItems} 项，已经满了。");

        var items = menu.Items.ToList();
        items[parentIndex] = parent with { Children = parent.Children.Append(item).ToList() };
        return (menu with { Items = items }, null);
    }

    /// <summary>
    /// 新建一个分组（带子菜单的项）。
    ///
    /// **分组必须带着第一个子项一起建。** 不允许建出空分组，原因是空分组会渲染成
    /// 一个既无子项、又无动作的扇区：点它什么都不发生、悬停也不展开任何东西，
    /// 而用户完全看不出为什么。宁可要求"先给它放一样东西"，
    /// 建好之后把鼠标移上去、在子环里点「＋」继续加就是了。
    ///
    /// 分组只加在第一层：子环里再放分组就是第三层，而当前只支持两层。
    /// </summary>
    public static (MenuSpec Menu, string? Error) AddGroup(
        MenuSpec menu, string? label, MenuItemSpec firstChild)
    {
        var trimmedLabel = label?.Trim();
        if (string.IsNullOrEmpty(trimmedLabel))
            return (menu, "分组名称不能为空。");

        var group = new MenuItemSpec
        {
            Label = trimmedLabel,
            Children = [firstChild],
        };

        return Add(menu, parentIndex: -1, group);
    }

    /// <summary>
    /// 移除一项。<paramref name="childIndex"/> 为 -1 表示移除第一层的项。
    ///
    /// 两层都有"不能删到空"的限制，但理由不同：
    /// 第一层删空了整个菜单就没东西可显示；
    /// 子层删空会让父项变成既无子项、又无动作的**死扇区** —— 点它什么都不发生，
    /// 而用户看不出为什么。与其留下一个点不动的扇区，不如明确拒绝。
    /// </summary>
    public static (MenuSpec Menu, string? Error) Remove(MenuSpec menu, int rootIndex, int childIndex)
    {
        if (rootIndex < 0 || rootIndex >= menu.Items.Count)
            return (menu, $"没有第 {rootIndex + 1} 项。");

        if (childIndex < 0)
        {
            if (menu.Items.Count <= 1)
                return (menu, "菜单至少要保留一项，不能删空。");

            var trimmed = menu.Items.ToList();
            trimmed.RemoveAt(rootIndex);
            return (menu with { Items = trimmed }, null);
        }

        var parent = menu.Items[rootIndex];
        if (childIndex >= parent.Children.Count)
            return (menu, $"「{parent.Label}」没有第 {childIndex + 1} 个子项。");

        if (parent.Children.Count <= 1)
            return (menu, $"「{parent.Label}」只剩这一项了，删掉它会变成一个点不动的扇区。");

        var items = menu.Items.ToList();
        var children = parent.Children.ToList();
        children.RemoveAt(childIndex);
        items[rootIndex] = parent with { Children = children };
        return (menu with { Items = items }, null);
    }

    /// <summary>改一项的名称。空名称会被拒绝 —— 界面上会显示成一块空白，用户认不出那是什么。</summary>
    public static (MenuSpec Menu, string? Error) Rename(
        MenuSpec menu, int rootIndex, int childIndex, string? label)
    {
        if (rootIndex < 0 || rootIndex >= menu.Items.Count)
            return (menu, $"没有第 {rootIndex + 1} 项。");

        var trimmedLabel = label?.Trim();
        if (string.IsNullOrEmpty(trimmedLabel))
            return (menu, "名称不能为空。");

        var items = menu.Items.ToList();

        if (childIndex < 0)
        {
            items[rootIndex] = items[rootIndex] with { Label = trimmedLabel };
            return (menu with { Items = items }, null);
        }

        var parent = items[rootIndex];
        if (childIndex >= parent.Children.Count)
            return (menu, $"「{parent.Label}」没有第 {childIndex + 1} 个子项。");

        var children = parent.Children.ToList();
        children[childIndex] = children[childIndex] with { Label = trimmedLabel };
        items[rootIndex] = parent with { Children = children };
        return (menu with { Items = items }, null);
    }

    /// <summary>取出一项。子项优先，<paramref name="childIndex"/> 为 -1 时取第一层的项。</summary>
    public static MenuItemSpec? Find(MenuSpec menu, int rootIndex, int childIndex)
    {
        if (rootIndex < 0 || rootIndex >= menu.Items.Count) return null;

        var item = menu.Items[rootIndex];
        if (childIndex < 0) return item;
        return childIndex < item.Children.Count ? item.Children[childIndex] : null;
    }
}
