using WinQuick.Core.Design;

namespace WinQuick.Core.Tests;

/// <summary>
/// 视觉规格的约束测试。
///
/// 这些断言锁的是**设计决策**，不是实现细节。每条背后都有依据：
/// 内存预算、HCI 文献、微软官方 Motion 规范。
/// 如果有人（包括我）改了 tokens.json 里某个数却不知道它为什么是那个值，这些测试会拦住。
/// </summary>
public class DesignTokensTests
{
    private static readonly DesignTokens Tokens = LoadRealTokens();

    private static DesignTokens LoadRealTokens()
    {
        // 从测试输出目录往上找仓库根，这样测的是盘上真正那份规格，
        // 而不是测试里另写一份 —— 后者测的是测试自己，毫无价值。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "design", "tokens.json");
            if (File.Exists(candidate))
                return DesignTokens.Parse(File.ReadAllText(candidate));
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "找不到 design/tokens.json。测试必须针对盘上真正那份规格运行。");
    }

    [Fact]
    public void RealTokensFile_PassesAllValidation()
    {
        Assert.Empty(Tokens.Validate());
    }

    // ---------- 内存预算 ----------

    [Fact]
    public void WindowSize_RespectsMemoryBudget()
    {
        // 实测模型：内存 ≈ 47MB 固定底 + 24 字节/DIP²。
        // 要落进 60MB 目标，窗口边长不能超过 740。详见 docs/M0-findings.md。
        Assert.True(Tokens.Radius.WindowSize <= 740,
            $"窗口边长 {Tokens.Radius.WindowSize} 超过内存预算上限 740。" +
            "窗口面积与内存成正比，放宽前必须重跑 --mem-probe 实测。");
    }

    // ---------- 几何 ----------

    [Fact]
    public void DeadZone_IsWithinPieMenuGuideline()
    {
        // Don Hopkins 的饼菜单规范：中心死区取外径的 15%-25%。
        // 死区是"取消"的落点，太小会误选、太大则浪费有效点击面积。
        var ratio = Tokens.Radius.DeadZone / (double)Tokens.Radius.Ring2Outer;
        Assert.InRange(ratio, 0.15, 0.25);
    }

    [Fact]
    public void Rings_AreStrictlyNested()
    {
        // 环必须严格外扩，否则命中测试的区间会重叠，点击落点变得不可预测。
        Assert.True(Tokens.Radius.DeadZone < Tokens.Radius.Ring1Outer);
        Assert.True(Tokens.Radius.Ring1Outer < Tokens.Radius.Ring2Inner);
        Assert.True(Tokens.Radius.Ring2Inner < Tokens.Radius.Ring2Outer);
    }

    [Fact]
    public void OuterRing_FitsInsideWindow()
    {
        Assert.True(Tokens.Radius.Ring2Outer <= Tokens.Radius.WindowSize / 2.0,
            "外环必须放得进窗口，否则圆盘会被裁掉");
    }

    [Fact]
    public void IconAndLabel_DoNotCollideRadially()
    {
        // 图标偏内、文字偏外，两者半径必须拉开足够的距离才不会有视觉重叠。
        Assert.True(Tokens.Radius.LabelRadius - Tokens.Radius.IconRadius >= Tokens.Radius.IconSize * 0.9,
            $"图标半径 {Tokens.Radius.IconRadius:0.#} 与文字半径 {Tokens.Radius.LabelRadius:0.#} 太近，会叠在一起");
    }

    [Fact]
    public void IconAndLabel_StayInsideFirstRing()
    {
        var band = Tokens.Radius.Ring1Outer - Tokens.Radius.DeadZone;
        Assert.InRange(Tokens.Radius.IconRadius, Tokens.Radius.DeadZone, Tokens.Radius.Ring1Outer);
        Assert.InRange(Tokens.Radius.LabelRadius, Tokens.Radius.DeadZone, Tokens.Radius.Ring1Outer);
        Assert.True(band > Tokens.Radius.IconSize, "环带宽度必须大于图标直径");
    }

    // ---------- 动效（对照微软官方 Motion 参数表）----------

    [Fact]
    public void Entrance_UsesFluentDirectEntranceCurve()
    {
        // 官方规范：直接入场用 cubic-bezier(0,0,0,1)，时长 167ms。
        // 必须能被精确解析 —— 退化成默认曲线的话动画观感会明显不对，而且很隐蔽。
        Assert.Equal(167, Tokens.Motion.EntranceDurationMs);
        Assert.True(MotionTokens.TryParseEasing(Tokens.Motion.EntranceEasing, out var p));
        Assert.Equal((0.0, 0.0, 0.0, 1.0), p);
    }

    [Fact]
    public void Exit_UsesFluentDirectExitCurve()
    {
        Assert.Equal(167, Tokens.Motion.ExitDurationMs);
        Assert.True(MotionTokens.TryParseEasing(Tokens.Motion.ExitEasing, out var p));
        Assert.Equal((0.0, 0.0, 0.0, 1.0), p);
    }

    [Fact]
    public void Entrance_StartsSlightlySmallerAndNeverOvershoots()
    {
        // 缩放从略小开始，且不能大于 1 —— 大于 1 会"弹过头"，在唤起场景里显得廉价。
        Assert.InRange(Tokens.Motion.EntranceFromScale, 0.8, 0.98);
        Assert.True(Tokens.Motion.ExitToScale <= 1.0);
    }

    [Fact]
    public void HoverHighlight_IsFastEnoughToFeelInstant()
    {
        // 悬停高亮是输入反馈而不是装饰，必须快到感觉不出延迟（官方 83ms 那一档）。
        Assert.InRange(Tokens.Motion.HoverHighlightMs, 0, 100);
    }

    // ---------- 交互（对照 HCI 文献）----------

    [Fact]
    public void HoverDelays_MatchTheEmpiricalFindings()
    {
        // Cockburn IJHCS 2024 实测：200ms 太快会误展开（70% 用户选择更慢的一侧），
        // 250ms 时偏好五五开。收起延迟要明显长于展开，否则指针划过边缘就会闪。
        Assert.InRange(Tokens.Interaction.HoverOpenDelayMs, 150, 300);
        Assert.InRange(Tokens.Interaction.HoverCloseDelayMs, 300, 500);
        Assert.True(Tokens.Interaction.HoverCloseDelayMs > Tokens.Interaction.HoverOpenDelayMs,
            "收起延迟必须大于展开延迟，否则指针掠过扇区边缘时子菜单会闪");
    }

    [Fact]
    public void ChildSectors_AreLargeEnoughToHit()
    {
        // 12 扇区父级下 90° 弧段里放 8 个子项只剩 11°，点不中。
        // 所以 childSpanDegrees 会被 childMinSectorDegrees × 子项数顶大。
        Assert.True(Tokens.Interaction.ChildMinSectorDegrees >= 20,
            "子扇区角度下限太小会导致子项细到点不中");
    }

    // ---------- 解析器本身 ----------

    [Theory]
    [InlineData("cubic-bezier(0,0,0,1)", 0.0, 0.0, 0.0, 1.0)]
    [InlineData("cubic-bezier(0.55, 0.55, 0, 1)", 0.55, 0.55, 0.0, 1.0)]
    [InlineData("cubic-bezier(1,0,1,1)", 1.0, 0.0, 1.0, 1.0)]
    public void EasingParser_HandlesCssSyntax(string text, double x1, double y1, double x2, double y2)
    {
        Assert.True(MotionTokens.TryParseEasing(text, out var p));
        Assert.Equal((x1, y1, x2, y2), p);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ease-in-out")]
    [InlineData("cubic-bezier(0,0,0)")]
    [InlineData("cubic-bezier(a,b,c,d)")]
    public void EasingParser_RejectsMalformedInput(string? text)
    {
        Assert.False(MotionTokens.TryParseEasing(text, out _));
    }

    [Fact]
    public void SecondRing_ClearsTheHoverBulge()
    {
        // 带子环的扇区恰恰就是被悬停（因此会向外鼓出）的那个。
        // 子环内缘若不给鼓出留出距离，悬停时会看到子环压在鼓出的扇区上。
        // 这条重叠是用户实际看到的，根因是这两个数值当初各自独立定、没人检查它们的关系。
        var gap = Tokens.Radius.Ring2Inner - Tokens.Radius.Ring1Outer;
        Assert.True(gap > Tokens.Motion.HoverBulgePx,
            $"子环内缘距环一外缘 {gap}，而悬停鼓出是 {Tokens.Motion.HoverBulgePx} —— 会重叠");
    }

    /// <summary>
    /// 子环铺满整圈时，最细的一档也必须还能放下文字。
    ///
    /// 这条把"环的尺寸"和"子项上限"绑在一起：谁把环改小、或把上限调大，
    /// 都应该在这里先失败，而不是等用户看到一圈挤成一团的标签。
    /// </summary>
    [Fact]
    public void Validate_CatchesChildRingTooSmallForItsItemCap()
    {
        // 子环带压到只剩一半宽：文字半径跟着塌下来，18° 的楔形就放不下字了。
        var cramped = Tokens with
        {
            Radius = Tokens.Radius with { Ring2Outer = 280 },
        };

        Assert.Contains(cramped.Validate(), p => p.Contains("子环按"));
    }

    /// <summary>现实那一份必须过 —— 否则上面那条测试只是在测一个不可能的状态。</summary>
    [Fact]
    public void RealTokens_SurviveTheChildRingLabelCheck()
    {
        Assert.Empty(Tokens.Validate());
    }

    [Fact]
    public void Validate_CatchesSecondRingOverlappingTheBulge()
    {
        // 构造一个内缘贴着环一外缘的配置，校验必须拦住它。
        var overlapping = new DesignTokens
        {
            Radius = new RadiusTokens { Ring1Outer = 200, Ring2Inner = 202, Ring2Outer = 350 },
            Motion = new MotionTokens { HoverBulgePx = 12 },
        };

        Assert.Contains(overlapping.Validate(), p => p.Contains("ring2Inner"));
        Assert.Contains(overlapping.Validate(), p => p.Contains("压在子环"));
    }

    [Fact]
    public void Validate_CatchesIconLabelCollision()
    {
        // 这条约束来自用户反馈「9 点 3 点方向的扇区文字和图标容易重叠」。
        // 根因是标签是轴对齐矩形：在 3 点/9 点方向，标签的『宽度』落在半径方向上，
        // 所以需要 radialGap >= 图标半宽 + 标签半宽。
        // 构造一个把图标推得离标签太近的配置，校验必须拦住它。
        var colliding = new DesignTokens
        {
            Radius = new RadiusTokens
            {
                DeadZone = 56,
                Ring1Outer = 200,
                LabelRadiusRatio = 0.65,
                IconRadiusRatio = 0.60,   // 图标几乎贴在标签上
            },
        };

        Assert.Contains(colliding.Validate(), p => p.Contains("径向间隔"));
    }

    [Fact]
    public void Validate_CatchesWindowSizeOverBudget()
    {
        // 校验函数必须真的会拦住超预算的窗口尺寸 —— 否则它只是一段装饰代码。
        var oversized = new DesignTokens { Radius = new RadiusTokens { WindowSize = 1000 } };
        Assert.Contains(oversized.Validate(), p => p.Contains("内存预算"));
    }

    [Fact]
    public void Validate_CatchesUnparseableEasing()
    {
        var broken = new DesignTokens { Motion = new MotionTokens { EntranceEasing = "ease" } };
        Assert.Contains(broken.Validate(), p => p.Contains("entranceEasing"));
    }
}
