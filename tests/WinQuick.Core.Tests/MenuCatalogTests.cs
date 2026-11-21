using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// 「添加到队列」的来源清单。
///
/// 这里锁的是三件容易写坏的事：层级路径的顺序与连接符、分组行与队列项必须被跳过、
/// 顶层项标签不能带前缀。漏跳过队列项会让清单里出现"点了等于没加"的行。
/// </summary>
public class MenuCatalogTests
{
    private static MenuItemSpec Leaf(string label, string type = "launch") => new()
    {
        Label = label,
        Action = new MenuAction { Type = type, Target = label },
    };

    private static MenuSpec ThreeLevels() => new(
        Id: "t",
        Name: "测试",
        Items:
        [
            Leaf("记事本"),
            new MenuItemSpec
            {
                Label = "影音",
                Children =
                [
                    Leaf("Audacity"),
                    new MenuItemSpec
                    {
                        Label = "播放器",
                        Children = [Leaf("MPC-HC"), Leaf("PotPlayer")],
                    },
                ],
            },
            Leaf("命令行", type: "open"),
        ]);

    [Fact]
    public void Flatten_ExpandsDepthFirst_InMenuOrder()
    {
        var entries = MenuCatalog.FlattenForPicker(ThreeLevels());

        Assert.Equal(
            ["记事本", "影音 › Audacity", "影音 › 播放器 › MPC-HC", "影音 › 播放器 › PotPlayer", "命令行"],
            entries.Select(e => e.DisplayLabel));
    }

    [Fact]
    public void Flatten_KeepsOriginalItemInstances()
    {
        var menu = ThreeLevels();
        var entries = MenuCatalog.FlattenForPicker(menu);

        // 节点是原对象：选择面板要把它的动作原样交给队列，不能是复制品。
        Assert.Same(menu.Items[1].Children[0], entries[1].Item);
        Assert.Same(menu.Items[1].Children[1].Children[1], entries[3].Item);
    }

    [Fact]
    public void Flatten_SkipsGroupRows()
    {
        var entries = MenuCatalog.FlattenForPicker(ThreeLevels());

        // 「影音」「播放器」没有动作，不能作为候选；它们只出现在子项的标签里。
        Assert.DoesNotContain(entries, e => e.DisplayLabel == "影音");
        Assert.DoesNotContain(entries, e => e.DisplayLabel == "影音 › 播放器");
        Assert.DoesNotContain(entries, e => e.Item.Action is null);
    }

    [Fact]
    public void Flatten_SkipsQueueItems_ButKeepsTheirChildren()
    {
        var menu = new MenuSpec(
            Id: "t",
            Name: "测试",
            Items:
            [
                Leaf("启动队列", type: "queue"),
                new MenuItemSpec
                {
                    Label = "外层",
                    Children = [Leaf("内层", type: "queue"), Leaf("正常")],
                },
            ]);

        var labels = MenuCatalog.FlattenForPicker(menu).Select(e => e.DisplayLabel);

        Assert.Equal(["外层 › 正常"], labels);
    }

    [Fact]
    public void Flatten_EmptyMenu_ReturnsEmpty()
    {
        var entries = MenuCatalog.FlattenForPicker(new MenuSpec("t", "空", []));

        Assert.Empty(entries);
    }

    [Fact]
    public void Flatten_TopLevelLabelHasNoPrefix()
    {
        var entries = MenuCatalog.FlattenForPicker(ThreeLevels());

        Assert.Equal("记事本", entries[0].DisplayLabel);
        Assert.DoesNotContain(MenuCatalog.PathSeparator, entries[0].DisplayLabel);
    }
}
