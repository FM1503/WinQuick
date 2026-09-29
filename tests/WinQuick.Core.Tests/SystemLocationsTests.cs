using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// 系统位置清单的约束测试。
///
/// 这些断言锁的是"清单本身别写坏"：重复项、空名、形态不对的解析名。
/// **能不能解析**是机器相关的，所以放在探针里核对（那份清单允许某些名字在本机不存在，
/// 界面会把它筛掉），这里只测纯数据的自洽性。
/// </summary>
public class SystemLocationsTests
{
    private static readonly IReadOnlyList<SystemLocation> All = SystemLocations.VirtualFolders;

    [Fact]
    public void HasRecycleBinAndThisPc()
    {
        Assert.Contains(All, l => l.Label == "回收站");
        Assert.Contains(All, l => l.Label == "此电脑");
    }

    [Fact]
    public void NoDuplicateTargets()
    {
        var dupes = All.GroupBy(l => l.Target, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(dupes);
    }

    [Fact]
    public void EveryEntryHasLabelAndTarget()
    {
        foreach (var location in All)
        {
            Assert.False(string.IsNullOrWhiteSpace(location.Label), "有空标签的项");
            Assert.False(string.IsNullOrWhiteSpace(location.Target), $"「{location.Label}」没有目标");
        }
    }

    /// <summary>虚拟文件夹必须真是 shell 命名空间形态，否则会被当成路径去打开。</summary>
    [Fact]
    public void EveryVirtualFolderLooksLikeShellNamespace()
    {
        foreach (var location in All)
            Assert.True(location.IsShellNamespace, $"「{location.Label}」不是 shell 命名空间形态：{location.Target}");
    }

    [Fact]
    public void VirtualFolderAction_IsShellType()
    {
        var recycle = All.Single(l => l.Label == "回收站");
        var action = recycle.ToAction();

        Assert.Equal("shell", action.Type);
        Assert.Equal("shell:RecycleBinFolder", action.Target);
    }

    /// <summary>真实路径（已知文件夹、盘符）必须走 open，不能走 shell。</summary>
    [Fact]
    public void RealPathAction_IsOpenType()
    {
        var downloads = new SystemLocation("下载", @"C:\Users\someone\Downloads");
        var action = downloads.ToAction();

        Assert.False(downloads.IsShellNamespace);
        Assert.Equal("open", action.Type);
    }
}
