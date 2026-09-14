using WinQuick.Core.Geometry;

namespace WinQuick.Core.Tests;

/// <summary>
/// 这些测试锁住的是「几何语义」，不是实现细节。
///
/// 背景：最初把 0° 实现成第一个扇区的起始边界，导致正上方是一条扇区缝隙 ——
/// 视觉上最该有一个扇区的位置反而什么都没有。这个缺陷是靠逐像素分析渲染出的 PNG
/// 才发现的（沿 0° 做半径剖面，从内到外全是背景色）。下面的测试确保它不会被改回去。
/// </summary>
public class RadialLayoutTests
{
    private const double Tolerance = 1e-9;

    // ---------- 角度约定：0° = 正上，顺时针 ----------

    [Fact]
    public void FromPolar_ZeroDegrees_PointsUp()
    {
        var p = Vec2.FromPolar(100, 0);
        Assert.Equal(0, p.X, Tolerance);
        Assert.Equal(-100, p.Y, Tolerance);   // 屏幕坐标 y 向下，所以正上是负 y
    }

    [Fact]
    public void FromPolar_NinetyDegrees_PointsRight()
    {
        var p = Vec2.FromPolar(100, 90);
        Assert.Equal(100, p.X, Tolerance);
        Assert.Equal(0, p.Y, Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(359.9)]
    public void FromPolar_RoundTripsThroughAngleDeg(double deg)
    {
        var back = Vec2.FromPolar(137, deg).AngleDeg;
        Assert.Equal(deg, back, 1e-6);
    }

    // ---------- 核心语义：0° 是扇区中心，不是边界 ----------

    [Fact]
    public void Distribute_EightSectors_FirstSectorIsCentredOnZero()
    {
        var spans = RadialLayout.Distribute(8);

        Assert.Equal(8, spans.Length);
        Assert.Equal(-22.5, spans[0].StartDeg, Tolerance);
        Assert.Equal(22.5, spans[0].EndDeg, Tolerance);
        Assert.Equal(0, spans[0].CenterDeg, Tolerance);
    }

    [Fact]
    public void Distribute_EvenSpacing_SectorCentresLandOnAxisAlignedAngles()
    {
        // 4 扇区时 N/E/S/W 必须正好落在扇区中心 —— 这是"说哪个方向就选中哪个"的基础。
        var four = RadialLayout.Distribute(4);
        Assert.Equal([0.0, 90.0, 180.0, 270.0],
            four.Select(s => Math.Round(SectorSpan.Normalize(s.CenterDeg), 6)).ToArray());

        var twelve = RadialLayout.Distribute(12);
        Assert.Equal(30.0, SectorSpan.Normalize(twelve[1].CenterDeg), 1e-6);
        Assert.Equal(90.0, SectorSpan.Normalize(twelve[3].CenterDeg), 1e-6);
    }

    [Fact]
    public void Distribute_ZeroDegrees_IsInsideFirstSector_NotInAGap()
    {
        // 这就是当初的缺陷：正上方必须落在扇区里。
        var spans = RadialLayout.Distribute(8);
        Assert.True(spans[0].Contains(0), "0° 应属于第 0 个扇区");
        Assert.True(spans[0].Contains(22), "22° 仍应属于第 0 个扇区");
        Assert.False(spans[0].Contains(23), "23° 已越过边界，不该属于第 0 个扇区");
    }

    [Fact]
    public void Distribute_SectorBoundariesAreBetweenSectors_NotOnZero()
    {
        var spans = RadialLayout.Distribute(8);
        // 22.5° 是两个扇区的分界，属于后一个扇区
        Assert.False(spans[0].Contains(22.5));
        Assert.True(spans[1].Contains(22.5));
    }

    [Fact]
    public void Distribute_SectorsCoverFullCircleExactly()
    {
        var spans = RadialLayout.Distribute(8);
        var total = spans.Sum(s => s.SweepDeg);
        Assert.Equal(360.0, total, 1e-9);
    }

    [Fact]
    public void Distribute_RespectsSectorCap()
    {
        Assert.Equal(RadialLayout.MaxSectors, RadialLayout.Distribute(99).Length);
    }

    // ---------- 命中测试 ----------

    private static readonly double Inner = 56;
    private static readonly double Outer = 210;
    private static readonly SectorSpan[] Spans = RadialLayout.Distribute(8);

    [Fact]
    public void HitTest_InsideInnerRadius_IsDeadZone()
    {
        var hit = RadialLayout.HitTest(Vec2.FromPolar(10, 30), Inner, Outer, Spans);
        Assert.Equal(HitZone.DeadZone, hit.Zone);
        Assert.Equal(-1, hit.Index);
    }

    [Fact]
    public void HitTest_BeyondOuterRadius_IsOutside()
    {
        var hit = RadialLayout.HitTest(Vec2.FromPolar(Outer + 5, 30), Inner, Outer, Spans);
        Assert.Equal(HitZone.Outside, hit.Zone);
    }

    [Theory]
    [InlineData(0, 0)]      // 正上
    [InlineData(45, 1)]
    [InlineData(90, 2)]     // 正右
    [InlineData(180, 4)]    // 正下
    [InlineData(270, 6)]    // 正左
    [InlineData(315, 7)]
    public void HitTest_StraightUpIsSectorZero_AndClockwiseProgression(double angle, int expectedIndex)
    {
        var mid = (Inner + Outer) / 2;
        var hit = RadialLayout.HitTest(Vec2.FromPolar(mid, angle), Inner, Outer, Spans);
        Assert.Equal(HitZone.Sector, hit.Zone);
        Assert.Equal(expectedIndex, hit.Index);
    }

    // ---------- 显式中心角 ----------

    [Fact]
    public void FromAngles_AllNull_MatchesEvenDistribution()
    {
        var (spans, err) = RadialLayout.FromAngles([null, null, null, null, null, null, null, null]);
        Assert.Null(err);
        Assert.Equal(RadialLayout.Distribute(8), spans);
    }

    [Fact]
    public void FromAngles_EvenSpacing_GivesExactSectorCentres()
    {
        var (spans, err) = RadialLayout.FromAngles([0, 90, 180, 270]);
        Assert.Null(err);
        Assert.Equal(0, spans[0].CenterDeg, 1e-6);
        Assert.Equal(90, spans[1].CenterDeg, 1e-6);
        Assert.Equal(180, spans[2].CenterDeg, 1e-6);
        Assert.Equal(270, spans[3].CenterDeg, 1e-6);
    }

    [Fact]
    public void FromAngles_NonIncreasingAngles_IsRejected()
    {
        var (_, err) = RadialLayout.FromAngles([0, 90, 45]);
        Assert.NotNull(err);
        Assert.Contains("严格递增", err);
    }

    [Fact]
    public void FromAngles_TooManySectors_IsRejected()
    {
        var (_, err) = RadialLayout.FromAngles(new double?[13]);
        Assert.NotNull(err);
        Assert.Contains("超过上限", err);
    }

    [Fact]
    public void FromAngles_MixedExplicitAndImplicit_InterpolatesTheGaps()
    {
        // 两项显式（0° 和 180°），中间两项应插值到 60° 和 120°，
        // 边界落在相邻角度的中点 30° / 90° / 150° / 270°。
        var (spans, err) = RadialLayout.FromAngles([0, null, null, 180]);
        Assert.Null(err);

        // 用「某角度落在哪一项」来验证，而不是「该项中心正好等于某值」：
        // 非均匀分布下后者不可能对所有项同时成立（见下一个测试）。
        Assert.Equal(0, IndexContaining(spans, 0));
        Assert.Equal(1, IndexContaining(spans, 60));
        Assert.Equal(2, IndexContaining(spans, 120));
        Assert.Equal(3, IndexContaining(spans, 180));

        Assert.False(spans[0].Contains(30.5), "30° 之后应已属于下一项");
        Assert.True(spans[1].Contains(30.5));
        Assert.False(spans[1].Contains(90.5));
        Assert.True(spans[2].Contains(90.5));
    }

    private static int IndexContaining(SectorSpan[] spans, double angleDeg)
    {
        for (var i = 0; i < spans.Length; i++)
            if (spans[i].Contains(angleDeg)) return i;
        return -1;
    }

    [Fact]
    public void FromAngles_UnevenSpacing_DeclaredAngleStillFallsInsideItsOwnSector()
    {
        // 这是真正要保证的契约：非均匀分布时"扇区中心"无法对所有项同时成立，
        // 但用户写 angle: 0 就必须点正上方能选中它。
        var (spans, err) = RadialLayout.FromAngles([0, null, null, 180]);
        Assert.Null(err);

        Assert.True(spans[0].Contains(0), "声明 angle=0 的项必须包含正上方");
        Assert.True(spans[3].Contains(180), "声明 angle=180 的项必须包含正下方");
    }

    // ---------- 子环 ----------

    [Fact]
    public void ChildArc_StaysCentredOnParentDirection()
    {
        var arc = RadialLayout.ChildArc(parentCenterDeg: 90, childCount: 3);
        Assert.Equal(90, arc.CenterDeg, 1e-9);
    }

    [Fact]
    public void ChildArc_WidensWhenChildrenWouldBeTooThinToHit()
    {
        // 8 个子项 × 25° 下限 = 200°，应超过默认的 90°
        var arc = RadialLayout.ChildArc(parentCenterDeg: 0, childCount: 8, preferredSpanDeg: 90);
        Assert.Equal(200, arc.SweepDeg, 1e-9);
        Assert.True(arc.SweepDeg / 8 >= 25 - 1e-9, "每个子扇区必须至少 25°");
    }

    [Fact]
    public void ChildArc_IsCappedSoItCannotWrapIntoSillySpans()
    {
        var arc = RadialLayout.ChildArc(parentCenterDeg: 0, childCount: 12, preferredSpanDeg: 90);
        Assert.True(arc.SweepDeg <= 300 + 1e-9);
    }

    [Fact]
    public void DistributeWithin_DividesTheArcEvenly()
    {
        var arc = new SectorSpan(0, 90);
        var spans = RadialLayout.DistributeWithin(arc, 3);
        Assert.Equal(3, spans.Length);
        Assert.Equal(30, spans[0].SweepDeg, 1e-9);
        Assert.Equal(0, spans[0].StartDeg, 1e-9);
        Assert.Equal(90, spans[2].EndDeg, 1e-9);
    }

    // ---------- SectorSpan ----------

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(450, 90)]
    public void Normalize_WrapsIntoZeroToThreeSixty(double input, double expected)
    {
        Assert.Equal(expected, SectorSpan.Normalize(input), 1e-9);
    }

    [Fact]
    public void Contains_HandlesSpanCrossingZero()
    {
        var span = new SectorSpan(350, 10);   // 跨 0° 的 20° 扇区
        Assert.True(span.Contains(355));
        Assert.True(span.Contains(0));
        Assert.True(span.Contains(5));
        Assert.False(span.Contains(20));
        Assert.False(span.Contains(340));
    }
}
