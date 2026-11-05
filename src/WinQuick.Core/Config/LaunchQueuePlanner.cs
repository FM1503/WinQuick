using System;

namespace WinQuick.Core.Config;

/// <summary>
/// 启动队列里的一步。队列项被排成步骤后，执行器只需按步骤执行，
/// 不再关心「哪几项该合并打开」这件事。
/// </summary>
public abstract record LaunchStep
{
    private LaunchStep()
    {
    }

    /// <summary>照原样执行一项（launch / url / shell，以及目标已不存在的 open）。</summary>
    public sealed record Single(MenuItemSpec Item) : LaunchStep;

    /// <summary>
    /// 一批文件夹目录，**一次** explorer 调用打开。列表内保持原队列顺序。
    /// </summary>
    public sealed record Folders(IReadOnlyList<MenuItemSpec> Items) : LaunchStep;
}

/// <summary>
/// 把队列项排成步骤，决定哪些文件夹合并成一次打开。
///
/// **为什么收集全部文件夹，而不是只合并相邻的？**
/// 文件夹之间几乎不存在先后依赖 —— 用户配「一堆工作目录」的心智就是「开一柜子抽屉」，
/// 先开哪个都一样；程序项才有先后讲究（先起容器再起客户端、先起 IDE 再起终端）。
/// 所以只有文件夹享受合并，而且不要求它们在清单里挨在一起：
/// 要求相邻的话，用户为了凑一个窗口得专门调整清单顺序，而那个顺序本来是用来表达程序依赖的。
/// 合并后的批次放在**第一个文件夹项原来的位置** —— 对不参与合并的程序项来说，
/// 文件夹「什么时候出现」保持不变，只是同时出现的那些变成了一个窗口里的多个标签页。
///
/// **前提：依赖 Win11 22H2+ 的多路径单窗口标签页行为。**
/// 实测确认 <c>explorer.exe "路径A" "路径B" "路径C"</c> 一次调用只开一个窗口、多个标签页。
/// Win10 上没有这套机制，多参数会退化成多个窗口 —— 结果仍然正确（该开的都开了），
/// 只是少了合并的好处，所以不专门适配。
/// </summary>
public static class LaunchQueuePlanner
{
    public static IReadOnlyList<LaunchStep> ToSteps(
        IReadOnlyList<MenuItemSpec> items, Func<MenuItemSpec, bool> isFolder)
    {
        var steps = new List<LaunchStep>(items.Count);
        var folders = new List<MenuItemSpec>();

        // 批次在**第一个文件夹项**处落座，之后遇到的文件夹只往里追加。
        // 如果按「遇到文件夹就新建一步」来写，同一次合并会被拆成多次 explorer 调用，
        // 也就回到了「一窗一个」的老样子。
        var hasFolders = false;

        foreach (var item in items)
        {
            if (isFolder(item))
            {
                if (!hasFolders)
                {
                    // 先落座、后填充：Folders 记的是**位置**，批次内容在下面一次性填入。
                    steps.Add(new LaunchStep.Folders(Array.Empty<MenuItemSpec>()));
                    hasFolders = true;
                }

                folders.Add(item);
                continue;
            }

            steps.Add(new LaunchStep.Single(item));
        }

        if (hasFolders)
        {
            // 快照而不是共享 List：调用方拿到的步骤不应再被后续逻辑改动。
            var snapshot = folders.ToArray();
            var index = steps.FindIndex(s => s is LaunchStep.Folders);
            steps[index] = new LaunchStep.Folders(snapshot);
        }

        return steps;
    }
}
