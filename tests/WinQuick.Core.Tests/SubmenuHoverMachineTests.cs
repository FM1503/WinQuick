using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using WinQuick.Core.Interaction;

namespace WinQuick.Core.Tests;

/// <summary>
/// 子菜单悬停状态机。
///
/// 这套规则靠人工点击验证几乎不可行（要精确控制 199ms 与 201ms 这种边界、
/// 还要模拟"斜着移向子弧"的轨迹），所以时钟由外部注入，这里用假时钟把边界一次测清。
/// </summary>
public class SubmenuHoverMachineTests
{
    private static readonly InteractionTokens Tokens = new()
    {
        HoverOpenDelayMs = 200,
        HoverCloseDelayMs = 350,
    };

    private static SubmenuHoverMachine NewMachine() => new(Tokens);

    private static HoverProbe OnParent(int index, long nowMs, bool hasChildren = true) =>
        new(HitZone.Sector, index, hasChildren, nowMs);

    private static HoverProbe OnLeaf(int index, long nowMs) =>
        new(HitZone.Sector, index, false, nowMs);

    private static HoverProbe OnChild(int index, long nowMs) =>
        new(HitZone.ChildSector, index, false, nowMs);

    private static HoverProbe InGap(long nowMs) =>
        new(HitZone.ChildArcGap, -1, false, nowMs);

    private static HoverProbe Outside(long nowMs) =>
        new(HitZone.Outside, -1, false, nowMs);

    // ---------- 展开延迟 ----------

    [Fact]
    public void HoverOnParent_DoesNotOpenBeforeDelayElapses()
    {
        var m = NewMachine();

        Assert.Equal(HoverAction.ScheduleOpen, m.OnPointer(OnParent(2, 0)));
        Assert.Equal(-1, m.OpenParent);

        // 199ms：差一点点，不能展开
        Assert.Equal(HoverAction.None, m.OnPointer(OnParent(2, 199)));
        Assert.Equal(-1, m.OpenParent);
    }

    [Fact]
    public void HoverOnParent_OpensOnceDelayElapses()
    {
        var m = NewMachine();

        m.OnPointer(OnParent(2, 0));
        Assert.Equal(HoverAction.OpenNow, m.OnPointer(OnParent(2, 200)));
        Assert.Equal(2, m.OpenParent);
    }

    [Fact]
    public void DelayKeepsCounting_EvenIfPointerDoesNotMove()
    {
        // 关键：即使指针一动不动，只要调用方持续轮询，延迟到期就该展开。
        // 若只在"下一次指针移动"时才检查，指针停住就永远不会展开 —— 这是个很容易写出的 bug。
        var m = NewMachine();

        m.OnPointer(OnParent(2, 0));
        Assert.Equal(HoverAction.None, m.OnPointer(OnParent(2, 100)));
        Assert.Equal(HoverAction.OpenNow, m.OnPointer(OnParent(2, 250)));
    }

    [Fact]
    public void LeafSector_OpensNothingEvenAfterDelay()
    {
        var m = NewMachine();

        m.OnPointer(OnLeaf(3, 0));
        Assert.Equal(HoverAction.None, m.OnPointer(OnLeaf(3, 500)));
        Assert.Equal(-1, m.OpenParent);
    }

    // ---------- 切换父扇区 ----------

    [Fact]
    public void SwitchingParent_ClosesOldAndRestartsDelayForNew()
    {
        var m = NewMachine();

        m.OnPointer(OnParent(0, 0));
        m.OnPointer(OnParent(0, 200));
        Assert.Equal(0, m.OpenParent);

        // 移到另一个有子项的扇区：先安排收起，并且重新开始计时
        Assert.Equal(HoverAction.CloseNow, m.OnPointer(OnParent(1, 210)));
        Assert.Equal(1, m.PendingParent);

        // 新的延迟还没到
        Assert.Equal(HoverAction.None, m.OnPointer(OnParent(1, 300)));
        Assert.Equal(HoverAction.OpenNow, m.OnPointer(OnParent(1, 410)));
        Assert.Equal(1, m.OpenParent);
    }

    // ---------- 斜向缓冲 ----------

    [Fact]
    public void MovingIntoTheChildArc_CancelsClosing()
    {
        // 这是斜向缓冲的核心：指针从父扇区斜着移向子弧，途中会越过缝隙与相邻扇区，
        // 那些落点不能导致子弧收起，否则它会在线索到达之前消失。
        var m = NewMachine();

        m.OnPointer(OnParent(0, 0));
        m.OnPointer(OnParent(0, 200));
        Assert.Equal(0, m.OpenParent);

        // 越过相邻扇区（叶子）—— 会安排收起
        Assert.Equal(HoverAction.ScheduleClose, m.OnPointer(OnLeaf(7, 220)));
        Assert.True(m.IsClosing);

        // 进入子弧范围 —— 必须取消收起
        Assert.Equal(HoverAction.None, m.OnPointer(OnChild(0, 260)));
        Assert.False(m.IsClosing);
        Assert.Equal(0, m.OpenParent);
    }

    [Fact]
    public void ChildArcGap_AlsoCountsAsInsideTheArc()
    {
        // 落在两个子扇区之间的缝隙上也算"在子弧内" —— 否则指针沿弧滑动经过缝隙时
        // 子弧会反复尝试收起。
        var m = NewMachine();

        m.OnPointer(OnParent(0, 0));
        m.OnPointer(OnParent(0, 200));
        m.OnPointer(OnParent(0, 210));   // 保持展开

        m.OnPointer(OnChild(0, 220));
        Assert.Equal(HoverAction.None, m.OnPointer(InGap(500)));
        Assert.False(m.IsClosing);
        Assert.Equal(0, m.OpenParent);
    }

    [Fact]
    public void ReturningToTheOpenParent_KeepsItOpen()
    {
        var m = NewMachine();

        m.OnPointer(OnParent(0, 0));
        m.OnPointer(OnParent(0, 200));
        m.OnPointer(OnChild(0, 300));

        // 又回到父扇区上 —— 不该开始收起，也不该重新计时
        Assert.Equal(HoverAction.None, m.OnPointer(OnParent(0, 900)));
        Assert.False(m.IsClosing);
        Assert.Equal(0, m.OpenParent);
    }

    // ---------- 收起延迟 ----------

    [Fact]
    public void MovingAway_SchedulesCloseAndHonoursDelay()
    {
        var m = NewMachine();

        m.OnPointer(OnParent(0, 0));
        m.OnPointer(OnParent(0, 200));

        Assert.Equal(HoverAction.ScheduleClose, m.OnPointer(Outside(300)));
        Assert.True(m.IsClosing);

        // 延迟未到不能收
        Assert.Equal(HoverAction.None, m.OnCloseTimerElapsed(649));
        Assert.False(m.CloseDelayElapsed(649));
        Assert.Equal(0, m.OpenParent);

        // 到期才收
        Assert.Equal(HoverAction.CloseNow, m.OnCloseTimerElapsed(650));
        Assert.Equal(-1, m.OpenParent);
    }

    [Fact]
    public void CloseDelayIsLongerThanOpenDelay()
    {
        // 若收起不比展开慢，指针掠过扇区边缘时子弧会闪 —— 这条是设计约束，不是巧合。
        Assert.True(Tokens.HoverCloseDelayMs > Tokens.HoverOpenDelayMs);
    }

    [Fact]
    public void MovingBackBeforeCloseDelayElapses_KeepsItOpen()
    {
        var m = NewMachine();

        m.OnPointer(OnParent(0, 0));
        m.OnPointer(OnParent(0, 200));
        m.OnPointer(Outside(300));

        // 在收起延迟内移回子弧
        Assert.Equal(HoverAction.None, m.OnPointer(OnChild(0, 500)));
        Assert.False(m.IsClosing);
        Assert.Equal(HoverAction.None, m.OnCloseTimerElapsed(900));
        Assert.Equal(0, m.OpenParent);
    }

    // ---------- 点击与复位 ----------

    [Fact]
    public void ClickOnParent_OpensImmediatelySkippingDelay()
    {
        var m = NewMachine();

        // 完全没有经过悬停延迟
        Assert.Equal(HoverAction.OpenNow, m.OpenImmediately(4));
        Assert.Equal(4, m.OpenParent);
    }

    [Fact]
    public void ClickingTheAlreadyOpenParent_ChangesNothing()
    {
        var m = NewMachine();

        m.OpenImmediately(4);
        Assert.Equal(HoverAction.None, m.OpenImmediately(4));
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var m = NewMachine();

        m.OnPointer(OnParent(1, 0));
        m.OnPointer(OnParent(1, 200));
        m.OnPointer(Outside(300));
        m.Reset();

        Assert.Equal(-1, m.OpenParent);
        Assert.Equal(-1, m.PendingParent);
        Assert.False(m.IsClosing);
    }

    [Fact]
    public void MovingOffAParentBeforeDelayElapses_CancelsPendingOpen()
    {
        var m = NewMachine();

        m.OnPointer(OnParent(2, 0));
        Assert.Equal(2, m.PendingParent);

        // 指针离开，移到别的扇区 —— 旧的待展开必须作废
        m.OnPointer(OnLeaf(5, 100));
        Assert.Equal(-1, m.PendingParent);

        // 即使时间早就过了，也不该展开
        Assert.Equal(HoverAction.None, m.OnPointer(OnLeaf(5, 500)));
        Assert.Equal(-1, m.OpenParent);
    }
}
