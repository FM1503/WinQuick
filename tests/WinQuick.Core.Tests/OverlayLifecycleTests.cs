using WinQuick.Core.Design;
using WinQuick.Core.Motion;

namespace WinQuick.Core.Tests;

/// <summary>
/// 覆盖层的唤出/退场顺序与入场计划。
///
/// 这些用例锁的是**顺序与不变量**，不是"跑起来对不对"：这个项目出过的真实缺陷里，
/// 有三个的根因就是顺序或状态耦合（挪到屏上先于落定起始状态 → 闪终态；
/// 休眠清空内容却没人恢复 → 空轮盘；编辑态由面板推导 → 被重建顺手算没）。
/// 顺序写在 App 的方法体里没人能复核，写成 Core 的列表就能被单测锁住。
/// </summary>
public class OverlayLifecycleTests
{
    // ---------------- 唤出序列 ----------------

    [Fact]
    public void Summon_LeavesDormancyBeforeAnythingElse()
    {
        foreach (var animations in new[] { true, false })
        {
            var steps = SummonSequence.Steps(animations);
            Assert.Equal(SummonStep.LeaveDormancy, steps[0]);
        }
    }

    [Fact]
    public void Summon_SettlesPreShowStateBeforeTheWindowIsShown()
    {
        // **这条是"先闪一帧终态"那个缺陷的回归锁。**
        // 窗口挪到屏上的那一瞬，屏幕上显示的是上一次提交的位图 ——
        // 所以"落定显示之前的状态"必须排在"显示"之前。
        foreach (var animations in new[] { true, false })
        {
            var steps = SummonSequence.Steps(animations);
            Assert.True(
                steps.IndexOf(SummonStep.SettlePreShowState) < steps.IndexOf(SummonStep.ShowAtCursor),
                "落定起始状态必须早于把窗口挪到屏上");
        }
    }

    [Fact]
    public void Summon_ReassertsTopmostAfterShowing()
    {
        var steps = SummonSequence.Steps(animationsEnabled: true);
        Assert.True(steps.IndexOf(SummonStep.ShowAtCursor) < steps.IndexOf(SummonStep.ReassertTopmost));
    }

    [Fact]
    public void Summon_PlaysEntranceOnlyWhenAnimationsAreEnabled()
    {
        Assert.Contains(SummonStep.PlayEntrance, SummonSequence.Steps(animationsEnabled: true));
        Assert.DoesNotContain(SummonStep.PlayEntrance, SummonSequence.Steps(animationsEnabled: false));
    }

    [Fact]
    public void Summon_AlwaysSettlesPreShowState_EvenWithoutAnimations()
    {
        // 关掉动画时也要落定 —— 只是那一步的目标从"入场起点"变成"终态"。
        // 少了它，关掉动画的用户会看到窗口停在上一轮退场留下的状态上。
        Assert.Contains(SummonStep.SettlePreShowState, SummonSequence.Steps(animationsEnabled: false));
    }

    // ---------------- 退场序列 ----------------

    [Fact]
    public void Dismiss_ReturnsFocusAndHidesDimBeforeWaitingForTheAnimation()
    {
        // **"退场不得阻塞动作执行"的回归锁**（Kando #650：退场淡出之后才启动应用，用户投诉延迟）。
        var steps = DismissSequence.Steps(animationsEnabled: true);
        var wait = steps.IndexOf(DismissStep.WaitExitAnimation);

        Assert.True(steps.IndexOf(DismissStep.ReturnFocusImmediately) < wait);
        Assert.True(steps.IndexOf(DismissStep.HideDimImmediately) < wait);
    }

    [Fact]
    public void Dismiss_AlwaysEndsInDormancy()
    {
        foreach (var animations in new[] { true, false })
        {
            var steps = DismissSequence.Steps(animations);
            Assert.Equal(DismissStep.EnterDormancy, steps[^1]);
        }
    }

    [Fact]
    public void Dismiss_SkipsTheWaitWhenAnimationsAreDisabled()
    {
        Assert.DoesNotContain(
            DismissStep.WaitExitAnimation, DismissSequence.Steps(animationsEnabled: false));
        Assert.Contains(
            DismissStep.EnterDormancy, DismissSequence.Steps(animationsEnabled: false));
    }

    // ---------------- 入场计划 ----------------

    [Fact]
    public void EntrancePlan_EveryChannelEndsExactlyAtItsTerminalValue()
    {
        // 入场动画留下的"没归位"在分层窗口上表现为永久歪一点/淡一点，
        // 而且只在之后某次操作时才暴露 —— 所以终点必须精确。
        var channels = EntrancePlan.Channels(new MotionTokens(), animationsEnabled: true);

        Assert.Contains(channels, c => c.Name == EntrancePlan.ScaleChannel && c.To == 1.0);
        Assert.Contains(channels, c => c.Name == EntrancePlan.FadeChannel && c.To == 1.0);
        Assert.Contains(channels, c => c.Name == EntrancePlan.RotateChannel && c.To == 0.0);
    }

    [Fact]
    public void EntrancePlan_IsEmptyWhenAnimationsAreDisabled()
    {
        Assert.Empty(EntrancePlan.Channels(new MotionTokens(), animationsEnabled: false));
    }

    [Fact]
    public void EntrancePlan_SkipsRotationWhenTheStartAngleIsZero()
    {
        var motion = new MotionTokens { EntranceFromRotateDeg = 0 };
        var channels = EntrancePlan.Channels(motion, animationsEnabled: true);

        Assert.DoesNotContain(channels, c => c.Name == EntrancePlan.RotateChannel);
    }

    [Fact]
    public void EntrancePlan_StartStateSharesTheDormantOpacity()
    {
        // **"停放与入场起点的不透明度相同"是分层窗口安全性的根据**：
        // 窗口挪回屏幕的那一瞬显示的是上一次提交的位图，而那个位图是"停放态"——
        // 只要它与入场起点的透明度相同（都是 0），就永远不可能露出上一轮的内容。
        var channels = EntrancePlan.Channels(new MotionTokens(), animationsEnabled: true);
        var fade = channels.Single(c => c.Name == EntrancePlan.FadeChannel);

        Assert.Equal(EntrancePlan.PreShowOpacity, fade.From, 1e-9);
        Assert.Equal(0.0, EntrancePlan.PreShowOpacity, 1e-9);
    }

    [Fact]
    public void EntrancePlan_TakesDurationsAndCurvesFromTheTokens()
    {
        var motion = new MotionTokens
        {
            EntranceScaleMs = 321,
            EntranceRotateMs = 654,
            EntranceDurationMs = 111,
            EntranceScaleEasing = "cubic-bezier(0.1,1.5,0.2,1)",
            EntranceRotateEasing = "cubic-bezier(0.16,1,0.3,1)",
            EntranceEasing = "cubic-bezier(0,0,0,1)",
            EntranceFromRotateDeg = -24,
        };

        var channels = EntrancePlan.Channels(motion, animationsEnabled: true);

        Assert.Equal(321, channels.Single(c => c.Name == EntrancePlan.ScaleChannel).DurationMs);
        Assert.Equal(654, channels.Single(c => c.Name == EntrancePlan.RotateChannel).DurationMs);
        Assert.Equal(111, channels.Single(c => c.Name == EntrancePlan.FadeChannel).DurationMs);
        Assert.Equal("cubic-bezier(0.1,1.5,0.2,1)", channels.Single(c => c.Name == EntrancePlan.ScaleChannel).Easing);
    }
}

/// <summary>给 <see cref="IReadOnlyList{T}"/> 的 IndexOf 用的小工具（Core 不引 System.Linq 的便利在这里补上）。</summary>
internal static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> list, T value)
    {
        for (var i = 0; i < list.Count; i++)
            if (EqualityComparer<T>.Default.Equals(list[i], value))
                return i;

        return -1;
    }
}
