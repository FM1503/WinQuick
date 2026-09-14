using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// 菜单增删改的规则。
///
/// 这些规则原先写在设置面板的界面代码里，靠人点界面确认。现在有两条编辑入口
/// （环里的「＋」、右键扇区菜单），规则必须只有一份 —— 放在 Core 里同时得到可测性。
///
/// 重点覆盖的两类是"索引移位"和"边界拒绝"：前者错了会删错项甚至越界，
/// 后者错了会让用户得到一个点不动的死扇区。
/// </summary>
public class MenuEditorTests
{
    private static MenuSpec SampleMenu() => new(
        Id: "t",
        Name: "测试",
        Items:
        [
            Item("A"),
            Container("组", [Item("子1"), Item("子2")]),
            Item("C"),
        ]);

    private static MenuItemSpec Item(string label) => new()
    {
        Label = label,
        Action = MenuAction.Launch("cmd.exe"),
    };

    private static MenuItemSpec Container(string label, IReadOnlyList<MenuItemSpec> children) => new()
    {
        Label = label,
        Children = children,
    };

    // ---------------------------------------------------------------- 加

    [Fact]
    public void Add_到第一层是追加在末尾()
    {
        var (menu, error) = MenuEditor.Add(SampleMenu(), -1, Item("D"));

        Assert.Null(error);
        Assert.Equal(["A", "组", "C", "D"], menu.Items.Select(i => i.Label));
    }

    /// <summary>末尾很关键：「＋」扇区永远排在所有项之后，靠的就是"加在末尾"。</summary>
    [Fact]
    public void Add_到子层是追加在该父项的子项末尾()
    {
        var (menu, error) = MenuEditor.Add(SampleMenu(), 1, Item("子3"));

        Assert.Null(error);
        Assert.Equal(["子1", "子2", "子3"], menu.Items[1].Children.Select(i => i.Label));
        // 其它项不受影响
        Assert.Equal(["A", "组", "C"], menu.Items.Select(i => i.Label));
    }

    [Fact]
    public void Add_不会修改传入的菜单()
    {
        var original = SampleMenu();
        MenuEditor.Add(original, -1, Item("D"));

        Assert.Equal(3, original.Items.Count);
    }

    [Fact]
    public void Add_第一层满了会被拒绝()
    {
        var full = SampleMenu() with
        {
            Items = Enumerable.Range(1, MenuEditor.MaxItems).Select(i => Item($"项{i}")).ToList(),
        };

        var (menu, error) = MenuEditor.Add(full, -1, Item("多出来的"));

        Assert.NotNull(error);
        Assert.Equal(MenuEditor.MaxItems, menu.Items.Count);
    }

    [Fact]
    public void Add_子层满了会被拒绝()
    {
        var full = new MenuSpec("t", "测试",
        [
            Container("组",
                Enumerable.Range(1, MenuEditor.MaxChildItems).Select(i => Item($"子{i}")).ToList()),
        ]);

        var (menu, error) = MenuEditor.Add(full, 0, Item("多出来的"));

        Assert.NotNull(error);
        Assert.Equal(MenuEditor.MaxChildItems, menu.Items[0].Children.Count);
    }

    /// <summary>
    /// 子层比第一层能装更多：子环可以铺满整圈，而第一层要留住
    /// 「上/右上/右/…」的钟面直觉，项数一多那种直觉就没了。
    /// </summary>
    [Fact]
    public void Add_子层上限比第一层宽松()
    {
        Assert.True(MenuEditor.MaxChildItems > MenuEditor.MaxItems,
            "子环能铺满整圈，上限应当高于第一层");

        // 子层一直加到上限都应当被接受。
        var menu = new MenuSpec("t", "测试", [Container("组", [])]);
        for (var i = 1; i <= MenuEditor.MaxChildItems; i++)
        {
            var (updated, error) = MenuEditor.Add(menu, 0, Item($"子{i}"));
            Assert.Null(error);
            menu = updated;
        }

        Assert.Equal(MenuEditor.MaxChildItems, menu.Items[0].Children.Count);

        // 再超一项就该被拒绝。
        var (tooMany, error2) = MenuEditor.Add(menu, 0, Item("多出来的"));
        Assert.NotNull(error2);
        Assert.Equal(MenuEditor.MaxChildItems, tooMany.Items[0].Children.Count);
    }

    [Fact]
    public void Add_父项索引不存在会被拒绝()
    {
        var (menu, error) = MenuEditor.Add(SampleMenu(), 9, Item("D"));

        Assert.NotNull(error);
        Assert.Equal(3, menu.Items.Count);
    }

    // ---------------------------------------------------------------- 删

    [Fact]
    public void Remove_第一层的中间项_后面的项索引前移()
    {
        // 这条是"索引移位"的直接体现：删掉中间的，原来的第 3 项变成第 2 项。
        var (menu, error) = MenuEditor.Remove(SampleMenu(), 1, -1);

        Assert.Null(error);
        Assert.Equal(["A", "C"], menu.Items.Select(i => i.Label));
    }

    [Fact]
    public void Remove_子项只影响该父项()
    {
        var (menu, error) = MenuEditor.Remove(SampleMenu(), 1, 0);

        Assert.Null(error);
        Assert.Equal(["子2"], menu.Items[1].Children.Select(i => i.Label));
        Assert.Equal(3, menu.Items.Count);
    }

    [Fact]
    public void Remove_不允许把第一层删空()
    {
        var single = new MenuSpec("t", "测试", [Item("唯一")]);

        var (menu, error) = MenuEditor.Remove(single, 0, -1);

        Assert.NotNull(error);
        Assert.Single(menu.Items);
    }

    /// <summary>
    /// 子层删空的后果与第一层不同：父项会变成一个既无子项、又无动作的扇区，
    /// 点它什么都不发生而用户看不出原因。所以必须拒绝，而不是留下一个死扇区。
    /// </summary>
    [Fact]
    public void Remove_不允许把子层删空_否则父项会变成死扇区()
    {
        var menu = new MenuSpec("t", "测试", [Container("组", [Item("唯一子项")])]);

        var (updated, error) = MenuEditor.Remove(menu, 0, 0);

        Assert.NotNull(error);
        Assert.Single(updated.Items[0].Children);
    }

    [Fact]
    public void Remove_索引越界会被拒绝且不改动菜单()
    {
        var menu = SampleMenu();

        Assert.NotNull(MenuEditor.Remove(menu, 5, -1).Error);
        Assert.NotNull(MenuEditor.Remove(menu, 1, 7).Error);
        Assert.NotNull(MenuEditor.Remove(menu, -1, -1).Error);

        Assert.Equal(3, menu.Items.Count);
        Assert.Equal(2, menu.Items[1].Children.Count);
    }

    // ---------------------------------------------------------------- 改

    [Fact]
    public void Rename_改第一层()
    {
        var (menu, error) = MenuEditor.Rename(SampleMenu(), 0, -1, "新名称");

        Assert.Null(error);
        Assert.Equal("新名称", menu.Items[0].Label);
        // 其它项不受影响
        Assert.Equal(["新名称", "组", "C"], menu.Items.Select(i => i.Label));
    }

    [Fact]
    public void Rename_改子项()
    {
        var (menu, error) = MenuEditor.Rename(SampleMenu(), 1, 1, "改名后的子项");

        Assert.Null(error);
        Assert.Equal(["子1", "改名后的子项"], menu.Items[1].Children.Select(i => i.Label));
    }

    [Fact]
    public void Rename_名称两端空白会被去掉()
    {
        var (menu, error) = MenuEditor.Rename(SampleMenu(), 0, -1, "  空格包围  ");

        Assert.Null(error);
        Assert.Equal("空格包围", menu.Items[0].Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rename_空名称会被拒绝(string? label)
    {
        // 空名称会让扇区上出现一块认不出的空白。
        var (menu, error) = MenuEditor.Rename(SampleMenu(), 0, -1, label);

        Assert.NotNull(error);
        Assert.Equal("A", menu.Items[0].Label);
    }

    [Fact]
    public void Rename_索引越界会被拒绝()
    {
        Assert.NotNull(MenuEditor.Rename(SampleMenu(), 9, -1, "x").Error);
        Assert.NotNull(MenuEditor.Rename(SampleMenu(), 1, 9, "x").Error);
    }

    /// <summary>改名不该碰图标与动作 —— 换名称是最常见的操作，动到别的字段会很隐蔽。</summary>
    [Fact]
    public void Rename_不影响图标与动作()
    {
        var menu = new MenuSpec("t", "测试",
        [
            new MenuItemSpec { Label = "A", IconPath = @"C:\a.exe", Action = MenuAction.Launch(@"C:\a.exe") },
        ]);

        var (updated, _) = MenuEditor.Rename(menu, 0, -1, "B");

        Assert.Equal("B", updated.Items[0].Label);
        Assert.Equal(@"C:\a.exe", updated.Items[0].IconPath);
        Assert.Equal(@"C:\a.exe", updated.Items[0].Action?.Target);
    }

    // ---------------------------------------------------------------- 查

    [Fact]
    public void Find_子项索引为负时取第一层的项()
    {
        var menu = SampleMenu();

        Assert.Equal("组", MenuEditor.Find(menu, 1, -1)?.Label);
        Assert.Equal("子2", MenuEditor.Find(menu, 1, 1)?.Label);
        Assert.Null(MenuEditor.Find(menu, 9, -1));
        Assert.Null(MenuEditor.Find(menu, 1, 9));
    }

    /// <summary>
    /// 连续操作一轮之后仍然自洽：加一项 → 删一项 → 改一项，
    /// 每步都用同一个索引语义。分步正确但组合起来错位，是索引处理最典型的问题。
    /// </summary>
    [Fact]
    public void 连续增删改一轮之后菜单仍然自洽()
    {
        var menu = SampleMenu();

        (menu, _) = MenuEditor.Add(menu, -1, Item("D"));       // [A, 组, C, D]
        (menu, _) = MenuEditor.Add(menu, 1, Item("子3"));       // 加到「组」上
        (menu, _) = MenuEditor.Remove(menu, 0, -1);             // 删掉 A，「组」前移到索引 0
        (menu, _) = MenuEditor.Rename(menu, 0, 0, "改过的子1");

        Assert.Equal(["组", "C", "D"], menu.Items.Select(i => i.Label));
        Assert.Equal(["改过的子1", "子2", "子3"], menu.Items[0].Children.Select(i => i.Label));
    }
}
