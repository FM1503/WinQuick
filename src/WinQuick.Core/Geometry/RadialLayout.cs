namespace WinQuick.Core.Geometry;

/// <summary>
/// 轮盘的角度分配与命中测试。
///
/// 角度约定：0° = 正上，顺时针为正（钟面直觉）。
///
/// 关键语义决定：**0° 是第一个扇区的中心线，不是扇区边界。**
/// 一开始按「0° 作为起始边界」实现，结果正上方成了一条扇区缝隙 ——
/// 这是靠逐像素分析渲染结果才发现的（沿 0° 半径剖面全是背景色）。
/// 中心对齐才符合直觉：说「把浏览器放在最上面」就应该真的在最上面，
/// 而不是骑在 8 点与 12 点的分界线上。4/8 扇区时这也让 N/E/S/W 恰好落在扇区中心。
///
/// 其余取舍的依据见方案第七节参数表：8 扇区是经验上限、深度 8 扇区下不超过 2 层、
/// 子菜单角度与父级同向对齐（后者是第二层能形成肌肉记忆的前提）。
/// </summary>
public static class RadialLayout
{
    /// <summary>扇区数硬上限。Kando 官方经验值 8、绝不超 12；CHI'93 也支持 8 是拐点。</summary>
    public const int MaxSectors = 12;

    /// <summary>
    /// 均匀分布。第一个扇区以 startOffsetDeg 为中心，其余等分整圈。
    /// 返回顺序即索引顺序。
    /// </summary>
    public static SectorSpan[] Distribute(int count, double startOffsetDeg = 0)
    {
        if (count <= 0) return [];
        if (count > MaxSectors) count = MaxSectors;

        var sweep = 360.0 / count;
        var spans = new SectorSpan[count];
        for (var i = 0; i < count; i++)
        {
            var center = startOffsetDeg + i * sweep;
            spans[i] = new SectorSpan(center - sweep / 2.0, center + sweep / 2.0);
        }
        return spans;
    }

    /// <summary>
    /// 由各扇区的角度构造扇区。
    ///
    /// 契约（写准了很重要，否则会做出"以为在中心、实际偏了一半"的菜单）：
    /// - <paramref name="angles"/> 里给的是**该项的角度位置**，0° = 正上。
    /// - 扇区边界取相邻两项角度的中点。
    /// - 因此：均匀分布时 angle 恰好是该扇区的中心；非均匀分布时是最近邻域划分，
    ///   但**声明了 angle 的项一定包含该角度** —— 这才是交互上真正要保证的性质
    ///   （用户写 angle: 0，点正上方就必须选中它）。
    ///
    /// 全为 null 时均匀分布。给出部分显式值时，未指定的项在相邻显式项之间均匀插值。
    /// 显式值必须严格递增 —— 否则扇区交叠、命中测试失去意义。
    /// 这比 Kando 宽松：angle 可省略，手写配置友好得多。
    /// </summary>
    public static (SectorSpan[] Spans, string? Error) FromAngles(IReadOnlyList<double?> angles)
    {
        var n = angles.Count;
        if (n == 0) return ([], null);
        if (n > MaxSectors) return ([], $"扇区数 {n} 超过上限 {MaxSectors}");

        if (angles.All(c => c is null))
            return (Distribute(n), null);

        // 校验显式角度严格递增。
        var last = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            if (angles[i] is not { } c) continue;
            var norm = SectorSpan.Normalize(c);
            if (norm <= last)
                return ([], $"第 {i + 1} 项的 angle={c} 未严格大于前一项的 {last:0.#}；" +
                            "显式角度必须严格递增且首项不小于 0");
            last = norm;
        }

        var specified = new List<int>();
        for (var i = 0; i < n; i++)
            if (angles[i] is not null) specified.Add(i);

        // 解出每一项的角度：显式值直接用，其余在相邻显式值之间等分。
        var resolved = new double[n];
        for (var s = 0; s < specified.Count; s++)
        {
            var from = specified[s];
            var toIdx = s + 1 < specified.Count ? specified[s + 1] : specified[0] + n;
            var fromDeg = SectorSpan.Normalize(angles[from]!.Value);
            var toDeg = s + 1 < specified.Count
                ? SectorSpan.Normalize(angles[toIdx]!.Value)
                : fromDeg + 360.0;

            var gaps = toIdx - from;
            for (var k = 0; k < gaps; k++)
                resolved[(from + k) % n] = fromDeg + k * (toDeg - fromDeg) / gaps;
        }

        // 由角度导出边界：相邻两项角度的中点是它们的公共边界。
        var spans = new SectorSpan[n];
        for (var i = 0; i < n; i++)
        {
            var prev = resolved[(i - 1 + n) % n];
            var cur = resolved[i];
            var next = resolved[(i + 1) % n];
            if (prev >= cur) prev -= 360.0;      // 跨越 0° 时回退一圈
            if (next <= cur) next += 360.0;
            spans[i] = new SectorSpan((prev + cur) / 2.0, (cur + next) / 2.0);
        }
        return (spans, null);
    }

    /// <summary>
    /// 子环最多几项。
    ///
    /// 与第一层的 <see cref="MaxSectors"/>=12 不同，这里允许子环撑满整圈，
    /// 因为子环只有一层、没有更深的分支，项多是用户把一类工具收在一起的正常用法
    /// （"开发工具"里放十几个都很常见）。12 项按每项 25° 只能铺 300°，
    /// 剩 60° 空着，那就是"想围成整圈却围不成"。
    ///
    /// 20 这个数不是拍的，是两处约束里更紧的那个：
    /// - **标签**：子环文字半径约 304、图标 30，当扇区降到 18° 时，
    ///   按弦长算出的可用半宽正好降到与"不压到图标"的限值相等（约 35）。
    ///   再细分下去楔形就不再是瓶颈了，所以 18° 是"再分也不损失文字宽度"的转折点。
    /// - **点中**：18° 在子环内缘（218）上是约 68px 的弧长 —— 远大于任何可点性下限。
    ///
    /// 360 / 18 = 20。这条换算由 <c>DesignTokens.Validate</c> 的核对锁住：
    /// 谁要是把环改小到 18° 时文字宽度不够，那里会先报出来。
    /// </summary>
    public const int MaxChildSectors = 20;

    /// <summary>
    /// 子环的角度跨度：以父扇区中心线为中线，默认 90°，
    /// 但保证每个子扇区至少 25° —— 否则 12 扇区父级下子项会细到点不中。
    ///
    /// 上限是**整圈 360°**：项数够多时子环就铺满一整圈，不再是一个贴着父扇区的弧。
    /// 到那一步"与父级同向对齐"这个性质自然消失（整圈没有方向可言），
    /// 换来的是项数不再被弧宽卡住。
    /// </summary>
    public static SectorSpan ChildArc(double parentCenterDeg, int childCount, double preferredSpanDeg = 90.0)
    {
        var span = Math.Min(Math.Max(preferredSpanDeg, childCount * 25.0), 360.0);
        return new SectorSpan(parentCenterDeg - span / 2.0, parentCenterDeg + span / 2.0);
    }

    /// <summary>子环内部各子扇区的划分：在给定弧段内等分，并把弧段中线作为第一个子项的中心。</summary>
    public static SectorSpan[] DistributeWithin(SectorSpan arc, int count)
    {
        if (count <= 0) return [];
        var sweep = arc.SweepDeg / count;
        var spans = new SectorSpan[count];
        for (var i = 0; i < count; i++)
            spans[i] = new SectorSpan(arc.StartDeg + i * sweep, arc.StartDeg + (i + 1) * sweep);
        return spans;
    }

    /// <summary>
    /// 命中测试。坐标系是相对轮盘中心的偏移，y 轴向下（屏幕坐标）。
    /// </summary>
    public static HitResult HitTest(Vec2 offset, double innerRadius, double outerRadius, SectorSpan[] spans)
    {
        var angle = offset.AngleDeg;
        var distance = offset.Length;

        // 死区：中心不选中任何项，所以「点一次中心 = 取消」是自然行为，不需要额外规则。
        if (distance < innerRadius)
            return new HitResult(HitZone.DeadZone, -1, distance, angle);

        if (distance > outerRadius)
            return new HitResult(HitZone.Outside, -1, distance, angle);

        for (var i = 0; i < spans.Length; i++)
            if (spans[i].Contains(angle))
                return new HitResult(HitZone.Sector, i, distance, angle);

        return new HitResult(HitZone.Outside, -1, distance, angle);
    }
}
