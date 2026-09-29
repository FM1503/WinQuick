using WinQuick.Core.Config;
using WinQuick.Core.Interaction;

namespace WinQuick.Core.Tests;

/// <summary>
/// 「两级滑动窗口」的层级算术。
///
/// 这套算术是新的交互模型的地基：同时只显示两级，环 1 = 父层、环 2 = 当前层，
/// 点环 2 的分组扇区就再进一层（旧的环 2 整层搬到环 1）。
/// 靠手点验证很慢（走到第三层要三次点击）而且容易漏，所以在这里逐项锁住。
/// </summary>
public class LevelPathTests
{
    /// <summary>三层菜单：一级 3 项，其中第 1 项有 4 个子项，子项里第 0 个还有 2 个孙项。</summary>
    private static readonly MenuSpec Menu = new(
        Id: "test",
        Name: "测试菜单",
        Items:
        [
            Leaf("一级0"),
            Group("一级1",
            [
                Group("二级0",
                [
                    Leaf("三级0"),
                    Leaf("三级1"),
                ]),
                Leaf("二级1"),
                Leaf("二级2"),
                Leaf("二级3"),
            ]),
            Leaf("一级2"),
        ]);

    private static MenuItemSpec Leaf(string label) => new() { Label = label };

    private static MenuItemSpec Group(string label, IReadOnlyList<MenuItemSpec> children)
        => new() { Label = label, Children = children };

    private static string Labels(IReadOnlyList<MenuItemSpec> items)
        => string.Join(",", items.Select(i => i.Label));

    [Fact]
    public void 第一层时环1是一级_环2为空()
    {
        var path = LevelPath.AtRoot;

        Assert.Equal(0, path.Depth);
        Assert.Equal(0, path.Ring1Level);
        Assert.Equal(-1, path.ParentIndexInRing1);
        Assert.Equal("一级0,一级1,一级2", Labels(path.Ring1Items(Menu)));
        Assert.Empty(path.Ring2Items(Menu));
    }

    [Fact]
    public void 下钻一层后环1仍是一级_环2是被点那一项的子项()
    {
        var path = LevelPath.AtRoot.SwitchBranch(1);

        Assert.Equal(1, path.Depth);
        Assert.Equal(0, path.Ring1Level);
        Assert.Equal(1, path.ParentIndexInRing1);
        Assert.Equal("一级0,一级1,一级2", Labels(path.Ring1Items(Menu)));
        Assert.Equal("二级0,二级1,二级2,二级3", Labels(path.Ring2Items(Menu)));
    }

    /// <summary>
    /// 用户那条需求的原话：「点击第二层的分组扇区进入第三层，此时原来的第二层移到第一层的位置」。
    /// 这条就是它的机器化表达 —— 逐项、顺序都要一致。
    /// </summary>
    [Fact]
    public void 进第三层时_旧的环2整层搬到环1且逐项一致()
    {
        var before = LevelPath.AtRoot.SwitchBranch(1);
        var oldRing2 = Labels(before.Ring2Items(Menu));

        var after = before.Descend(0);

        Assert.Equal("二级0,二级1,二级2,二级3", oldRing2);
        Assert.Equal(oldRing2, Labels(after.Ring1Items(Menu)));
        Assert.Equal("三级0,三级1", Labels(after.Ring2Items(Menu)));
    }

    [Fact]
    public void 环1里换分支_深度不变_环2换成它的子项()
    {
        var deep = LevelPath.AtRoot.SwitchBranch(1).Descend(0);
        Assert.Equal(2, deep.Depth);

        // 环 1 现在是「二级0..二级3」，点其中的「二级3」（下标 3，它是叶子）
        // —— 这里只验层级算术：换分支后环 1 不变、环 2 换成它这一支的子项。
        var switched = deep.SwitchBranch(0);

        Assert.Equal(2, switched.Depth);
        Assert.Equal("二级0,二级1,二级2,二级3", Labels(switched.Ring1Items(Menu)));
        Assert.Equal("三级0,三级1", Labels(switched.Ring2Items(Menu)));
    }

    [Fact]
    public void 返回上一层与返回第一层()
    {
        var deep = LevelPath.AtRoot.SwitchBranch(1).Descend(0).Descend(1);
        Assert.Equal(3, deep.Depth);
        Assert.Equal("三级0,三级1", Labels(deep.Ring1Items(Menu)));

        var up = deep.Back();
        Assert.Equal(2, up.Depth);
        Assert.Equal("二级0,二级1,二级2,二级3", Labels(up.Ring1Items(Menu)));

        Assert.True(up.Back().Back().IsRoot);
        Assert.Equal(LevelPath.AtRoot, up.Back().Back());

        // 已经在第一层时再返回不改变任何东西
        Assert.True(LevelPath.AtRoot.Back().IsRoot);
    }

    [Fact]
    public void 路径不够长时_那一层是空的()
    {
        // 深度只有 1，却去问第 2 层：必须是空，而不是悄悄回退成一级
        var path = LevelPath.AtRoot.SwitchBranch(1);
        Assert.Empty(MenuLevels.ItemsAt(Menu, 2, path.Steps));

        // 深度 0 问第 1 层同理
        Assert.Empty(MenuLevels.ItemsAt(Menu, 1, LevelPath.AtRoot.Steps));
    }

    [Fact]
    public void 越界的下标不会抛异常_只是取不到东西()
    {
        Assert.Empty(MenuLevels.ItemsAt(Menu, 1, [99]));
        Assert.Empty(MenuLevels.ItemsAt(Menu, 1, [-1]));
        Assert.Null(MenuLevels.Resolve(Menu, [99]));
        Assert.Null(MenuLevels.Resolve(Menu, []));
    }

    /// <summary>
    /// 任意深度都能解析出项 —— 旧实现只认两级，第三级的分组项点下去会走到
    /// 「还没有配置动作」（`ResidentHost` 里只取 `Items[parent].Children[child]`）。
    /// </summary>
    [Fact]
    public void 三级项能按路径解析出来()
    {
        var path = LevelPath.AtRoot.SwitchBranch(1).Descend(0);

        var resolved = MenuLevels.Resolve(Menu, path.StepsToRing2(1));

        Assert.NotNull(resolved);
        Assert.Equal("三级1", resolved!.Label);
    }

    [Fact]
    public void 环1里的项也能算出它的完整路径()
    {
        var path = LevelPath.AtRoot.SwitchBranch(1).Descend(0);

        // 环 1 里第 3 项 = 二级3，它的路径是 [1,3]
        Assert.Equal([1, 3], path.StepsToRing1(3));

        var resolved = MenuLevels.Resolve(Menu, path.StepsToRing1(3));
        Assert.Equal("二级3", resolved!.Label);
    }

    [Fact]
    public void 环2的父项是路径最后一段指向的那一项()
    {
        // 深度 1：父项就是一级里被展开的那一项
        var one = LevelPath.AtRoot.SwitchBranch(1);
        Assert.Equal("一级1", one.Ring2Parent(Menu)?.Label);
        Assert.Equal("二级0,二级1,二级2,二级3",
            Labels(one.Ring2Parent(Menu)?.Children ?? []));

        // 深度 2：父项是二级里被展开的那一项
        var two = one.Descend(0);
        Assert.Equal("二级0", two.Ring2Parent(Menu)?.Label);
        Assert.Equal("三级0,三级1", Labels(two.Ring2Parent(Menu)?.Children ?? []));

        // 深度 0：没有父项
        Assert.Null(LevelPath.AtRoot.Ring2Parent(Menu));
    }

    [Fact]
    public void 环2的项数等于父项的子项数()
    {
        var path = LevelPath.AtRoot.SwitchBranch(1);
        Assert.Equal(path.Ring2Parent(Menu)!.Children.Count, path.Ring2Items(Menu).Count);

        var deeper = path.Descend(0);
        Assert.Equal(deeper.Ring2Parent(Menu)!.Children.Count, deeper.Ring2Items(Menu).Count);
    }

    [Fact]
    public void 路径越界时父项解析不出来_但不会抛()
    {
        var bad = LevelPath.Of([99, 0]);
        Assert.Null(bad.Ring2Parent(Menu));
        Assert.Empty(bad.Ring2Items(Menu));
        Assert.Empty(bad.Ring1Items(Menu));
    }

    [Fact]
    public void 同一份路径相等_且可作字典键()
    {
        var a = LevelPath.AtRoot.SwitchBranch(1).Descend(0);
        var b = LevelPath.Of([1, 0]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, LevelPath.Of([1, 1]));
        Assert.NotEqual(a, LevelPath.AtRoot);

        var map = new Dictionary<LevelPath, string> { [a] = "x" };
        Assert.True(map.ContainsKey(b));
    }

    [Fact]
    public void 真实菜单一级12项时_下钻与返回都不越界()
    {
        // 用真实菜单里的一项（它的子项数与最长标签最有代表性）
        var menu = MenuSpec.BuiltInDefault;
        var withChildren = menu.Items
            .Select((item, index) => (item, index))
            .Where(t => t.item.HasChildren)
            .ToList();
        Assert.NotEmpty(withChildren);

        foreach (var (item, index) in withChildren)
        {
            var path = LevelPath.AtRoot.SwitchBranch(index);
            Assert.Equal(item.Children.Count, path.Ring2Items(menu).Count);
            Assert.True(path.Back().IsRoot);

            // 子项里若还有分组，再进一层：环 1 必须换成刚才的环 2
            var grandParent = item.Children
                .Select((child, i) => (child, i))
                .FirstOrDefault(t => t.child.HasChildren);
            if (grandParent.child is null) continue;

            var deeper = path.Descend(grandParent.i);
            Assert.Equal(
                Labels(path.Ring2Items(menu)),
                Labels(deeper.Ring1Items(menu)));
            Assert.Equal(grandParent.child.Children.Count, deeper.Ring2Items(menu).Count);
        }
    }
}
