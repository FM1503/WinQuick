using WinQuick.Core.Motion;

namespace WinQuick.Core.Tests;

/// <summary>
/// 逐项依次出现的节奏计算。
///
/// 需要单测的原因：这项参数随项数变（8 项 / 12 项 / 20 个子项），而"顺便压一下交错"
/// 这种判断最容易写错又不报错 —— 错了的样子是"项多时整轮拖到一秒以上，用户读成卡顿"。
/// </summary>
public class StaggerRevealTests
{
    [Fact]
    public void Plan_KeepsGivenStagger_WhenItFitsTheSweepBudget()
    {
        // 8 项 × 55ms = 385 的预算（460 − 190 = 270 装不下），所以这里用宽松的上限
        var (stagger, total) = StaggerReveal.Plan(8, 55, 190, 800);

        Assert.Equal(55, stagger, 1e-9);
        Assert.Equal(55 * 7 + 190, total, 1e-9);
    }

    [Fact]
    public void Plan_CompressesStagger_SoTheSweepStaysWithinTheCap()
    {
        // 默认值：8 项、55ms 交错、190ms 单项、上限 460ms
        var (stagger, total) = StaggerReveal.Plan(8, 55, 190, 460);

        Assert.True(total <= 460 + 1e-9, $"整轮 {total} 超过了上限 460");
        Assert.Equal((460 - 190) / 7.0, stagger, 1e-9);
        Assert.Equal(460, total, 1e-9);
    }

    [Fact]
    public void Plan_HandsOutAllTheBudgetToStagger_WhenThereAreManyItems()
    {
        // 20 个子项：压缩后每格只有十几毫秒，整轮仍在预算内
        var (stagger, total) = StaggerReveal.Plan(20, 45, 150, 460);

        Assert.InRange(stagger, 5, 45);
        Assert.True(total <= 460 + 1e-9);
    }

    [Fact]
    public void Plan_DegradesGracefully_OnDegenerateCounts()
    {
        Assert.Equal((0.0, 1.0), StaggerReveal.Plan(0, 55, 0, 460));
        Assert.Equal((0.0, 190.0), StaggerReveal.Plan(1, 55, 190, 460));
    }

    [Fact]
    public void Plan_NeverReturnsNegativeStagger_WhenTheCapIsBelowItemDuration()
    {
        // 上限比单项时长还小（令牌校验会拦，但纯函数自己不能返回负数）
        var (stagger, total) = StaggerReveal.Plan(8, 55, 190, 100);

        Assert.True(stagger >= 0);
        Assert.True(total >= 190, "单项时长本身不能被压缩掉");
    }

    [Fact]
    public void RawProgress_StartsEachItemAtItsOwnDelay()
    {
        // 索引 3 的起始延迟是 3×55=165ms：在那之前一动不动，之后线性爬升，
        // 165+190=355ms 落定。
        Assert.Equal(0.0, StaggerReveal.RawProgress(3, 100, 55, 190), 1e-9);
        Assert.Equal(0.0, StaggerReveal.RawProgress(3, 165, 55, 190), 1e-9);
        Assert.Equal(0.5, StaggerReveal.RawProgress(3, 260, 55, 190), 1e-9);
        Assert.Equal(1.0, StaggerReveal.RawProgress(3, 355, 55, 190), 1e-9);
    }

    [Fact]
    public void RawProgress_IsClamped_SoOvershootComesFromTheCurveNotFromTime()
    {
        // 时间越界必须夹在 [0,1]：回弹要由缓动曲线给出，而不是靠时间外推
        Assert.Equal(0.0, StaggerReveal.RawProgress(0, -50, 55, 190), 1e-9);
        Assert.Equal(1.0, StaggerReveal.RawProgress(0, 100_000, 55, 190), 1e-9);
    }

    [Fact]
    public void ApplyGhost_MapsRawProgressOntoTheFirstState()
    {
        // 原始进度 0 ＝ 首态（残影），不是"什么都没有"：这是用户提的设计
        Assert.Equal(0.25, StaggerReveal.ApplyGhost(0, 0.25), 1e-9);
        Assert.Equal(0.1, StaggerReveal.ApplyGhost(0, 0.1), 1e-9);

        // 1 ＝ 完整实态（映射必须端点精确，否则入场结束后几何回不到静止态）
        Assert.Equal(1.0, StaggerReveal.ApplyGhost(1, 0.25), 1e-9);

        // 中间线性插值
        Assert.Equal(0.625, StaggerReveal.ApplyGhost(0.5, 0.25), 1e-9);
    }

    [Fact]
    public void ApplyGhost_TreatsNotStartedAsInvisible()
    {
        // 小于 0 ＝ 还没轮到它：必须彻底不显示。
        // 若这一条写错（把"没轮到"也映射成残影），盘面上会**同时亮着所有格的残影** ——
        // 那就不是"逐格出现"了，而肉眼未必看得出是 bug。
        // 约定：负值表示"还没轮到"（App 侧 StaggerRevealAnimator.NotStarted = -1）。
        Assert.Equal(0.0, StaggerReveal.ApplyGhost(-1, 0.25), 1e-9);
        Assert.Equal(0.0, StaggerReveal.ApplyGhost(-0.001, 0.25), 1e-9);
    }

    [Fact]
    public void ApplyGhost_DegradesToTheOldBehaviourWhenGhostIsZero()
    {
        // ghost = 0 就是旧版"从零长起"
        Assert.Equal(0.0, StaggerReveal.ApplyGhost(0, 0), 1e-9);
        Assert.Equal(1.0, StaggerReveal.ApplyGhost(1, 0), 1e-9);
    }
}
