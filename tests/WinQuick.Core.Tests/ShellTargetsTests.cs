using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// 右键菜单集成的纯逻辑。
///
/// 这里出错的方式都很隐蔽：命令行引号拼错会让整条命令失效（点了没反应），
/// 目的地解析错会把文件加进别的分组 —— 两者都不会报错，只能靠单测钉住。
/// </summary>
public class ShellTargetsTests
{
    private static MenuSpec Menu() => new(
        Id: "t",
        Name: "测试",
        Items:
        [
            Item("资源管理器"),
            Container("开发工具", [Item("VS Code"), Item("终端")]),
            Item("记事本"),
            Container("设计软件", [Item("Photoshop")]),
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

    // ---------------------------------------------------------------- 目的地

    [Fact]
    public void Build_一级环排在最前()
    {
        var targets = ShellTargets.Build(Menu());

        Assert.Equal(ShellTargets.RootValue, targets[0].Value);
        Assert.Equal("添加到一级环", targets[0].Label);
        Assert.Equal(-1, targets[0].ParentIndex);
    }

    [Fact]
    public void Build_只把带子项的分组列出来()
    {
        var targets = ShellTargets.Build(Menu());

        // 一级环 + 开发工具 + 设计软件；叶子项（资源管理器、记事本）不出现。
        Assert.Equal(3, targets.Count);
        Assert.Equal(["添加到一级环", "添加到「开发工具」", "添加到「设计软件」"],
            targets.Select(t => t.Label));
        Assert.Equal([-1, 1, 3], targets.Select(t => t.ParentIndex));
    }

    [Fact]
    public void Build_分组顺序与菜单顺序一致()
    {
        // 顺序不是随意的：右键菜单里用户靠位置找分组，位置必须稳定。
        var targets = ShellTargets.Build(Menu());

        Assert.True(targets[1].ParentIndex < targets[2].ParentIndex);
    }

    [Fact]
    public void Build_空菜单只有一级环()
    {
        var targets = ShellTargets.Build(new MenuSpec("t", "测试", []));

        Assert.Single(targets);
    }

    [Fact]
    public void Build_名字含引号的分组会被跳过并单独报出()
    {
        var menu = new MenuSpec("t", "测试",
        [
            Container("有\"引号\"的组", [Item("x")]),
            Container("正常组", [Item("y")]),
        ]);

        var targets = ShellTargets.Build(menu);

        Assert.Equal(["添加到一级环", "添加到「正常组」"], targets.Select(t => t.Label));
        Assert.Equal(["有\"引号\"的组"], ShellTargets.Skipped(menu));
    }

    [Fact]
    public void Build_超过上限时截断()
    {
        var many = Enumerable.Range(1, ShellTargets.MaxTargets + 5)
            .Select(i => Container($"组{i}", [Item($"子{i}")]))
            .ToList();

        var targets = ShellTargets.Build(new MenuSpec("t", "测试", many));

        Assert.Equal(ShellTargets.MaxTargets, targets.Count);
    }

    // ---------------------------------------------------------------- 命令行

    [Fact]
    public void BuildCommand_路径与占位符都带引号()
    {
        var target = new ShellTarget(ShellTargets.RootValue, "添加到一级环", -1);

        var command = ShellTargets.BuildCommand(@"C:\Program Files\WinQuick\WinQuick.exe", target);

        // 三处引号缺一不可：exe 路径、参数值、%1。任缺一处，
        // 只要路径里有空格就会静默失效（点了没反应）。
        Assert.Equal(@"""C:\Program Files\WinQuick\WinQuick.exe"" --add-to=""root"" ""%1""", command);
    }

    [Fact]
    public void BuildCommand_含中文与空格的分组名()
    {
        var target = new ShellTarget("group:开发 工具", "添加到「开发 工具」", 1);

        var command = ShellTargets.BuildCommand(@"C:\WQ\WinQuick.exe", target);

        Assert.Contains(@"--add-to=""group:开发 工具""", command);
        Assert.EndsWith(@"""%1""", command);
    }

    // ---------------------------------------------------------------- 解析

    [Fact]
    public void TryResolve_root_解析成第一层()
    {
        Assert.True(ShellTargets.TryResolve(Menu(), ShellTargets.RootValue, out var index, out var error));
        Assert.Equal(-1, index);
        Assert.Null(error);
    }

    [Fact]
    public void TryResolve_分组名解析成对应索引()
    {
        Assert.True(ShellTargets.TryResolve(Menu(), "group:设计软件", out var index, out _));
        Assert.Equal(3, index);
    }

    [Fact]
    public void TryResolve_与Build产出的值能对应上()
    {
        // 这一条把"生成"和"解析"绑在一起：两边各改一半也不会悄悄失配。
        var menu = Menu();

        foreach (var target in ShellTargets.Build(menu))
        {
            Assert.True(ShellTargets.TryResolve(menu, target.Value, out var index, out var error), error);
            Assert.Equal(target.ParentIndex, index);
        }
    }

    [Fact]
    public void TryResolve_分组改名后给出可读的原因()
    {
        // 注册表可能停留在上一轮写入的状态，这时必须说清"找不到"，
        // 而不是静默什么都不做 —— 后者会被当成功能坏了。
        var ok = ShellTargets.TryResolve(Menu(), "group:已改名的组", out var index, out var error);

        Assert.False(ok);
        Assert.Equal(-1, index);
        Assert.Contains("已改名的组", error);
        Assert.Contains("改名", error);
    }

    [Fact]
    public void TryResolve_叶子项不能当目的地()
    {
        // 「资源管理器」没有子项，不该成为目的地。
        Assert.False(ShellTargets.TryResolve(Menu(), "group:资源管理器", out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("乱写的东西")]
    [InlineData("root:x")]
    public void TryResolve_非法值都被拒绝而不是猜(string? value)
    {
        Assert.False(ShellTargets.TryResolve(Menu(), value, out var index, out var error));
        Assert.Equal(-1, index);
        Assert.NotNull(error);
    }

    // ---------------------------------------------------------------- 描述

    [Fact]
    public void Describe_用于托盘反馈()
    {
        var menu = Menu();

        Assert.Equal("一级环", ShellTargets.Describe(menu, -1));
        Assert.Equal("「开发工具」", ShellTargets.Describe(menu, 1));
    }
}
