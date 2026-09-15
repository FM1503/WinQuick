using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinQuick.Core.Design;

/// <summary>
/// 视觉规格模型。从 <c>design/tokens.json</c> 反序列化。
///
/// 为什么是"运行时读 JSON"而不是"用代码生成常量"：
/// 生成常量会引入第二种真源 —— 改 JSON 忘了重新生成，两边就悄悄分叉，
/// 而这种分叉只有到界面看起来不对的时候才会被发现。
/// 直接读 JSON 则不可能漂移：盘上那份就是唯一一份。
///
/// 代价是几何值从 <c>const</c> 变成运行时值，所以代码里不能再用它做
/// 默认参数值或属性上的常量。这个约束是可接受的。
/// </summary>
public sealed record DesignTokens
{
    public int Version { get; init; } = 1;

    [JsonPropertyName("color")] public ColorTokens Color { get; init; } = new();
    [JsonPropertyName("radius")] public RadiusTokens Radius { get; init; } = new();
    [JsonPropertyName("typography")] public TypographyTokens Typography { get; init; } = new();
    [JsonPropertyName("motion")] public MotionTokens Motion { get; init; } = new();
    [JsonPropertyName("perspective")] public PerspectiveTokens Perspective { get; init; } = new();
    [JsonPropertyName("interaction")] public InteractionTokens Interaction { get; init; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 未知字段（例如 "_comment"）一律忽略，便于在 JSON 里留说明。
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static DesignTokens Parse(string json) =>
        JsonSerializer.Deserialize<DesignTokens>(json, Options)
        ?? throw new InvalidOperationException("tokens.json 解析结果为空");

    /// <summary>校验那些被设计约束锁住的不变量。返回问题列表，空表示通过。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        // 内存约束：实测模型是 47MB 固定底 + 24 字节/DIP²，
        // 要落进 60MB 目标，窗口边长不能超过 740。详见 docs/M0-findings.md。
        const int maxWindowSize = 740;
        if (Radius.WindowSize > maxWindowSize)
        {
            problems.Add(
                $"radius.windowSize={Radius.WindowSize} 超过内存预算上限 {maxWindowSize}。" +
                "窗口面积与内存成正比，放宽前先重跑 --mem-probe。");
        }

        // 死区比例：Don Hopkins 的饼菜单规范建议 15%-25%。
        var deadZoneRatio = Radius.DeadZone / (double)Radius.Ring2Outer;
        if (deadZoneRatio is < 0.12 or > 0.28)
            problems.Add($"半径死区占外径 {deadZoneRatio:P0}，超出建议区间 15%-25% 附近");

        // 环必须严格外扩，否则命中测试会重叠。
        if (!(Radius.DeadZone < Radius.Ring1Outer))
            problems.Add("radius.deadZone 必须小于 ring1Outer");
        if (!(Radius.Ring1Outer < Radius.Ring2Inner))
            problems.Add("radius.ring1Outer 必须小于 ring2Inner（两环之间要留缝）");
        if (!(Radius.Ring2Inner < Radius.Ring2Outer))
            problems.Add("radius.ring2Inner 必须小于 ring2Outer");

        // 外环必须放得进窗口。
        if (Radius.Ring2Outer > Radius.WindowSize / 2.0)
            problems.Add($"radius.ring2Outer={Radius.Ring2Outer} 放不进边长 {Radius.WindowSize} 的窗口");

        // 缓动曲线必须能解析成三次贝塞尔，否则动画会静默退化成默认曲线。
        if (MotionTokens.TryParseEasing(Motion.EntranceEasing, out _) is false)
            problems.Add($"motion.entranceEasing 无法解析: {Motion.EntranceEasing}");
        if (MotionTokens.TryParseEasing(Motion.ExitEasing, out _) is false)
            problems.Add($"motion.exitEasing 无法解析: {Motion.ExitEasing}");

        // 子环内缘必须给悬停鼓出留出空间。
        // 带子环的扇区恰恰就是被悬停（因此会鼓出）的那个，两者留不出距离就会重叠。
        const double bulgeClearance = 4.0;
        if (Radius.Ring2Inner < Radius.Ring1Outer + Motion.HoverBulgePx + bulgeClearance)
        {
            problems.Add(
                $"radius.ring2Inner={Radius.Ring2Inner} 距环一外缘（{Radius.Ring1Outer}）太近：" +
                $"至少要为悬停鼓出（{Motion.HoverBulgePx}）留出 {bulgeClearance} 的余量，即 " +
                $"{Radius.Ring1Outer + Motion.HoverBulgePx + bulgeClearance}。否则悬停有子菜单的扇区时，" +
                "鼓出的部分会压在子环下面。");
        }

        // 图标与标签在 3 点/9 点方向不得重叠。
        // 那两个方向是最坏情况：扇区中心线水平，标签的『宽度』落在半径方向上，
        // 而 12 点/6 点方向落在半径方向上的是仅约 20px 的『高度』，所以不会冲突。
        // 约束：径向间隔 >= 图标半宽 + 标签半宽 + 余量
        var radialGap = Radius.LabelRadius - Radius.IconRadius;
        var halfIcon = Radius.IconSize / 2.0;
        var maxLabelHalfWidth = RadiusTokens.LabelMaxHalfWidth(Radius);
        const double clearance = 4.0;
        if (radialGap < halfIcon + maxLabelHalfWidth + clearance)
        {
            problems.Add(
                $"图标与标签的径向间隔 {radialGap:0.#} 不足：需要至少 " +
                $"{halfIcon + maxLabelHalfWidth + clearance:0.#}（图标半宽 {halfIcon:0.#} + " +
                $"标签半宽 {maxLabelHalfWidth:0.#} + 余量 {clearance}）。" +
                "否则在 3 点/9 点方向文字会压到图标上。");
        }

        // 长按阈值要有下限：太短会把普通点击误判成长按（点击是"执行程序"，
        // 误判的代价是用户想启动程序却进了编辑态）。上限则是因为超过 3 秒
        // 用户会以为程序没反应。
        if (Interaction.LongPressMs < 400 || Interaction.LongPressMs > 3000)
            problems.Add(
                $"interaction.longPressMs={Interaction.LongPressMs} 超出合理范围 400–3000ms；" +
                "过短会把普通点击误判成长按，过长会让用户以为没反应");

        // 子扇区的角度下限必须够大到能点中。
        if (Interaction.ChildMinSectorDegrees < 15)
            problems.Add($"interaction.childMinSectorDegrees={Interaction.ChildMinSectorDegrees} 过小，子项会细到点不中");

        // 子环的悬停鼓出要有地方鼓：外缘 + 鼓出量不能超出窗口。
        // 一级的鼓出由"子环内径要躲开一级的鼓出"这条间接保障（子环在一级外面），
        // 但子环自己是最外圈，鼓出去就是真的出界 —— 那会被窗口边缘裁掉，
        // 表现为"悬停子项时右边少一块"，而且只在特定方向上出现，很难看出来。
        if (Radius.Ring2Outer + Motion.HoverBulgePx > Radius.WindowSize / 2.0)
            problems.Add(
                $"ring2Outer={Radius.Ring2Outer} + hoverBulgePx={Motion.HoverBulgePx} " +
                $"超过了窗口半径 {Radius.WindowSize / 2.0} —— 子环悬停鼓出会被窗口边缘裁掉");

        // 子环撑满整圈时，最细的那一档（= MaxChildSectors）也必须还能放下文字。
        //
        // 这条把"环的尺寸"和"子项上限"绑在一起：以后谁把环改小了、
        // 或者把上限调大了，这里会先报出来，而不是等用户看到一排挤成一团的标签。
        // 判据取 30：比"不压到图标"的限值（约 35）留一点余量，
        // 且 30 的半宽对应两行文字每行 4~5 个汉字，是能读的下限。
        var fullCircleSweep = 360.0 / Geometry.RadialLayout.MaxChildSectors;
        var childLabelHalfWidth = RadiusTokens.LabelMaxHalfWidth(
            iconRadius: Radius.Ring2.At(Radius.IconRadiusRatio),
            labelRadius: Radius.Ring2.At(Radius.LabelRadiusRatio),
            iconSize: Radius.IconSize,
            labelSweepDegrees: fullCircleSweep,
            gapDegrees: Radius.SectorGapDegrees);

        if (childLabelHalfWidth < 30)
            problems.Add(
                $"子环按 {Geometry.RadialLayout.MaxChildSectors} 项铺满整圈时每项 {fullCircleSweep:0.#}°，" +
                $"此时标签半宽只剩 {childLabelHalfWidth:0.#}（要求 ≥ 30）—— 环太小或子项上限太大");

        return problems;
    }
}

public sealed record ColorTokens
{
    public string Dim { get; init; } = "#000000";
    public double DimAlpha { get; init; } = 0.4;
    public string SectorFill { get; init; } = "#202020";
    public double SectorFillAlpha { get; init; } = 0.92;
    public string SectorStroke { get; init; } = "#FFFFFF";
    public double SectorStrokeAlpha { get; init; } = 0.19;
    public string HoverFill { get; init; } = "#0078D4";
    public string HoverStroke { get; init; } = "#FFFFFF";
    public double HoverStrokeAlpha { get; init; } = 0.78;
    public string DeadZoneFill { get; init; } = "#121212";
    public string DeadZoneStroke { get; init; } = "#FFFFFF";
    public double DeadZoneStrokeAlpha { get; init; } = 0.24;
    public string Label { get; init; } = "#FFFFFF";
    public string Hint { get; init; } = "#FFFFFF";
    public double HintAlpha { get; init; } = 0.55;
    public string BadgeFill { get; init; } = "#000000";
    public double BadgeFillAlpha { get; init; } = 0.35;
}

public sealed record RadiusTokens
{
    public int WindowSize { get; init; } = 740;
    public int DeadZone { get; init; } = 52;
    public int Ring1Outer { get; init; } = 200;
    public int Ring2Inner { get; init; } = 206;
    public int Ring2Outer { get; init; } = 350;
    public double SectorGapDegrees { get; init; } = 1.2;

    /// <summary>图标绘制边长（DIP）。</summary>
    public int IconSize { get; init; } = 34;

    /// <summary>
    /// 图标中心所在的半径占第一环的比例。偏内侧，把外侧让给文字。
    /// 这个值不能太大：标签是轴对齐矩形，在 3 点/9 点方向它的『宽度』落在半径方向上，
    /// 若图标与标签的径向间隔小于「图标半宽 + 标签半宽」，两者就会叠在一起。
    /// </summary>
    public double IconRadiusRatio { get; init; } = 0.24;

    /// <summary>文字中心所在的半径占第一环的比例。</summary>
    public double LabelRadiusRatio { get; init; } = 0.65;

    /// <summary>标签最多折几行。折行让宽度减半，是同时解决「重叠」与「显示不全」的关键。</summary>
    public int LabelMaxLines { get; init; } = 2;

    /// <summary>行高倍率，用于估算多行标签的高度。</summary>
    public double LabelLineHeightFactor { get; init; } = 1.35;

    /// <summary>第一层（根）环的角度带。</summary>
    public Band Ring1 => new(DeadZone, Ring1Outer);

    /// <summary>第二层（子弧）环的角度带。</summary>
    public Band Ring2 => new(Ring2Inner, Ring2Outer);

    public double IconRadius => (DeadZone + (Ring1Outer - DeadZone) * IconRadiusRatio);
    public double LabelRadius => (DeadZone + (Ring1Outer - DeadZone) * LabelRadiusRatio);
    public double Ring1MidRadius => (DeadZone + Ring1Outer) / 2.0;

    /// <summary>
    /// 标签可用的半宽上限。取两者更严格的一个：
    /// ① 与图标不重叠所需的间隔（3 点/9 点方向的最坏情况）
    /// ② 扇区在标签半径处的弦长（文字不能越出自己的扇区）
    /// </summary>
    public static double LabelMaxHalfWidth(RadiusTokens r, int sectorCount = 8) =>
        LabelMaxHalfWidth(
            iconRadius: r.IconRadius,
            labelRadius: r.LabelRadius,
            iconSize: r.IconSize,
            labelSweepDegrees: 360.0 / Math.Max(1, sectorCount),
            gapDegrees: r.SectorGapDegrees);

    /// <summary>
    /// 通用的标签半宽上限。子弧的每个子扇区角度跨度差异很大
    /// （2 个子项时各 45°，8 个子项时各 25°），所以不能只按"扇区数"算。
    /// </summary>
    public static double LabelMaxHalfWidth(
        double iconRadius, double labelRadius, double iconSize,
        double labelSweepDegrees, double gapDegrees)
    {
        const double clearance = 4.0;

        // ① 不压到图标。3 点/9 点方向是最坏情况：那里标签的『宽度』落在半径方向上。
        var radialGap = labelRadius - iconRadius;
        var byIcon = radialGap - iconSize / 2.0 - clearance;

        // ② 不越出扇区。用弦长而不是弧长：文字是直的。
        var sweepRad = labelSweepDegrees * Math.PI / 180.0;
        var chord = 2 * labelRadius * Math.Sin(sweepRad / 2);
        var gapPixels = 2 * labelRadius * gapDegrees * Math.PI / 180.0;
        var byWedge = (chord - gapPixels - 6.0) / 2.0;

        return Math.Max(16.0, Math.Min(byIcon, byWedge));
    }
}

/// <summary>
/// 一个角度带（内外半径）。第一层环与第二层环共用同一套布局比例，
/// 所以把它们抽象成同一个概念，避免两处各写一份半径计算。
/// </summary>
public readonly record struct Band(double Inner, double Outer)
{
    public double Width => Outer - Inner;
    public double Mid => (Inner + Outer) / 2.0;

    /// <summary>按比例取半径：0 = 内缘，1 = 外缘。</summary>
    public double At(double ratio) => Inner + Width * ratio;
}

public sealed record TypographyTokens
{
    public string LabelFontFamily { get; init; } = "Microsoft YaHei UI, Segoe UI";
    public double LabelFontSize { get; init; } = 14;
    public double HintFontSize { get; init; } = 13;
    public int LabelMaxChars { get; init; } = 6;
}

public sealed record MotionTokens
{
    public int EntranceDurationMs { get; init; } = 167;
    public double EntranceFromScale { get; init; } = 0.9;
    public string EntranceEasing { get; init; } = "cubic-bezier(0,0,0,1)";
    public int ExitDurationMs { get; init; } = 167;
    public double ExitToScale { get; init; } = 0.96;
    public string ExitEasing { get; init; } = "cubic-bezier(0,0,0,1)";
    public int HoverHighlightMs { get; init; } = 83;

    /// <summary>悬停时扇区向外"鼓出"的像素数。</summary>
    public int HoverBulgePx { get; init; } = 12;

    /// <summary>鼓出动画时长。比颜色高亮略长，因为它是有形的位移，太快会显得跳。</summary>
    public int HoverBulgeMs { get; init; } = 130;

    /// <summary>子弧展开/收起时长。用官方规范里「已有元素点对点」的 167ms。</summary>
    public int ChildArcDurationMs { get; init; } = 167;

    /// <summary>子弧的缓动曲线。与入场用的是同一条（起步快、收尾缓），观感统一。</summary>
    public string ChildArcEasing { get; init; } = "cubic-bezier(0,0,0,1)";

    public int TopmostReassertMs { get; init; } = 800;

    /// <summary>
    /// 解析 CSS 的三次贝塞尔语法 <c>cubic-bezier(x1,y1,x2,y2)</c>。
    /// WPF 的 KeySpline 能精确表达同样的曲线，所以 Fluent 的曲线可以一比一搬过来。
    /// </summary>
    public static bool TryParseEasing(string? text, out (double X1, double Y1, double X2, double Y2) points)
    {
        points = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var open = text.IndexOf('(');
        var close = text.IndexOf(')');
        if (open < 0 || close < open) return false;

        var parts = text[(open + 1)..close]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 4) return false;
        if (!double.TryParse(parts[0], out var x1) || !double.TryParse(parts[1], out var y1) ||
            !double.TryParse(parts[2], out var x2) || !double.TryParse(parts[3], out var y2))
            return false;

        points = (x1, y1, x2, y2);
        return true;
    }
}

public sealed record PerspectiveTokens
{
    public bool Enabled { get; init; } = true;

    /// <summary>入场时的透视倾斜角度。刻意很小 —— 大了会像歪掉的转盘而不是浮起的圆盘。</summary>
    public double EntranceTiltDegrees { get; init; } = 8;

    /// <summary>透视强度（矩阵里 m34 的量级）。越大近大远小越夸张。</summary>
    public double EntranceDepth { get; init; } = 0.0012;
}

public sealed record InteractionTokens
{
    /// <summary>展开延迟的设计基线。用户没在配置里指定时用它。</summary>
    public const int DefaultOpenDelayMs = 200;

    /// <summary>收起延迟的设计基线。</summary>
    public const int DefaultCloseDelayMs = 350;

    public int HoverOpenDelayMs { get; init; } = DefaultOpenDelayMs;
    public int HoverCloseDelayMs { get; init; } = DefaultCloseDelayMs;
    public double ChildSpanDegrees { get; init; } = 90;

    /// <summary>
    /// 长按多久进入编辑态。用户指定 2 秒。
    ///
    /// 做成令牌而不是常量：长按阈值是最依赖手感的一类参数，
    /// 将来觉得"2 秒太长"时应该改一个数，而不是改代码。
    /// </summary>
    public int LongPressMs { get; init; } = 2000;
    public double ChildMinSectorDegrees { get; init; } = 25;
}
