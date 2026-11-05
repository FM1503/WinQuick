using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// 队列项 → 步骤的排布。
///
/// 这里锁住的是「哪些项会被合进同一次 explorer 调用」以及「合进去之后落在第几步」。
/// 位置这件事容易写错：合并批次必须呆在**第一个**文件夹项原来的位置，
/// 否则不参与合并的程序项会相对文件夹整体提前或推后，用户按清单顺序表达的
/// 「先起程序、再开目录」就被打乱了。
/// </summary>
public class LaunchQueuePlannerTests
{
    private static MenuItemSpec Program(string label) => new()
    {
        Label = label,
        Action = MenuAction.Launch($"C:\\Tools\\{label}.exe"),
    };

    private static MenuItemSpec Folder(string label) => new()
    {
        Label = label,
        Action = MenuAction.Open($"D:\\{label}"),
    };

    /// <summary>测试里的「是不是文件夹」判定按 label 前缀来，避免依赖磁盘上真实存在的目录。</summary>
    private static bool IsFolder(MenuItemSpec item) => item.Label.StartsWith("文件夹", StringComparison.Ordinal);

    private static IReadOnlyList<LaunchStep> Plan(params MenuItemSpec[] items) =>
        LaunchQueuePlanner.ToSteps(items, IsFolder);

    private static LaunchStep.Folders Folders(IReadOnlyList<LaunchStep> steps) =>
        steps.OfType<LaunchStep.Folders>().Single();

    private static string[] SingleLabels(IReadOnlyList<LaunchStep> steps) =>
        steps.OfType<LaunchStep.Single>().Select(s => s.Item.Label).ToArray();

    [Fact]
    public void Interleaved_FoldersMergeIntoFirstFolderPosition()
    {
        var steps = Plan(
            Program("程序"),
            Folder("文件夹A"),
            Program("坏程序"),
            Folder("文件夹B"),
            Folder("文件夹C"));

        Assert.Equal(3, steps.Count);
        Assert.IsType<LaunchStep.Single>(steps[0]);
        Assert.Equal("程序", ((LaunchStep.Single)steps[0]).Item.Label);
        Assert.IsType<LaunchStep.Folders>(steps[1]);
        Assert.IsType<LaunchStep.Single>(steps[2]);
        Assert.Equal("坏程序", ((LaunchStep.Single)steps[2]).Item.Label);

        // 批内顺序即原清单顺序。
        Assert.Equal(["文件夹A", "文件夹B", "文件夹C"], Folders(steps).Items.Select(f => f.Label));
        Assert.Equal(["程序", "坏程序"], SingleLabels(steps));
    }

    [Fact]
    public void AdjacentFoldersMergeIntoOneStep()
    {
        var steps = Plan(Folder("文件夹A"), Folder("文件夹B"));

        var step = Assert.Single(steps);
        Assert.Equal(["文件夹A", "文件夹B"], Assert.IsType<LaunchStep.Folders>(step).Items.Select(f => f.Label));
    }

    [Fact]
    public void SingleFolderStillBecomesFoldersStep()
    {
        // 单个文件夹走一次 explorer 调用，与改之前逐项执行等价 —— 不必为它单独留一条路径。
        var steps = Plan(Program("程序"), Folder("文件夹A"));

        Assert.Equal(2, steps.Count);
        Assert.IsType<LaunchStep.Single>(steps[0]);
        Assert.Equal(["文件夹A"], Folders(steps).Items.Select(f => f.Label));
    }

    [Fact]
    public void FoldersAfterAllProgramsStayAtTheEnd()
    {
        var steps = Plan(Program("程序1"), Program("程序2"), Folder("文件夹A"));

        Assert.Equal(["程序1", "程序2"], SingleLabels(steps));
        Assert.IsType<LaunchStep.Folders>(steps[2]);
    }

    [Fact]
    public void NoFolders_KeepsEverySingleInOrder()
    {
        var steps = Plan(Program("甲"), Program("乙"), Program("丙"));

        Assert.All(steps, s => Assert.IsType<LaunchStep.Single>(s));
        Assert.Equal(["甲", "乙", "丙"], SingleLabels(steps));
    }

    [Fact]
    public void EmptyList_YieldsNoSteps()
    {
        Assert.Empty(Plan());
    }

    [Fact]
    public void FoldersBatchKeepsOriginalSpecs()
    {
        // 失败汇报要用 label，所以批里必须是**原对象**，不能重建或只留 target。
        var a = Folder("文件夹A");
        var b = Folder("文件夹B");

        var batch = Folders(Plan(Program("程序"), a, b));

        Assert.Same(a, batch.Items[0]);
        Assert.Same(b, batch.Items[1]);
        Assert.Equal("D:\\文件夹A", batch.Items[0].Action!.Target);
    }
}
