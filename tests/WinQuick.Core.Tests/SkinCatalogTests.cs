using System.Text.Json.Nodes;
using WinQuick.Core.Design;

namespace WinQuick.Core.Tests;

/// <summary>
/// 皮肤的合并规则与「合并后仍然合法」的回归锁。
///
/// 这些断言测的是**盘上真正的** design/skins.json 与 design/tokens.json ——
/// 在测试里另写一份规格，测的就只是测试自己。
/// </summary>
public class SkinCatalogTests
{
    private static readonly string BaseJson = File.ReadAllText(Find("design", "tokens.json"));
    private static readonly SkinCatalog Catalog = SkinCatalog.Parse(File.ReadAllText(Find("design", "skins.json")));

    private static string Find(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"找不到 {string.Join('/', parts)}。测试必须针对盘上真正那份规格运行。");
    }

    private static DesignTokens MergeAndParse(string skinId) =>
        DesignTokens.Parse(Catalog.Merge(BaseJson, skinId));

    [Fact]
    public void Catalog_ContainsDefaultSkin()
    {
        Assert.True(Catalog.Contains(SkinCatalog.DefaultId));
        Assert.True(Catalog.All.Count >= 5, "皮肤太少了，换肤没有意义");
    }

    /// <summary>
    /// **最重要的一条**：每套皮肤合并后都必须通过全部不变量校验。
    ///
    /// 皮肤是"覆盖"，覆盖写错（比如把子环内缘压得离一号环太近、或者悬停鼓出超过了留缝）
    /// 会在合并结果里立刻违反约束 —— 这条测试就是让那种错误在提交前就红掉，
    /// 而不是等用户看到一圈挤在一起的文字。
    /// </summary>
    [Fact]
    public void EverySkin_PassesValidationAfterMerge()
    {
        var failures = new List<string>();
        foreach (var skin in Catalog.All)
        {
            var problems = MergeAndParse(skin.Id).Validate();
            if (problems.Count > 0)
                failures.Add($"{skin.Id}: {string.Join("；", problems)}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Merge_AppliesOverrides()
    {
        var merged = MergeAndParse("contrast");

        // 高对比档的立身之本：纯黑实心 + 2px 白描边 + 黄底黑字。
        Assert.Equal("#000000", merged.Color.SectorFill);
        Assert.Equal(1.0, merged.Color.SectorFillAlpha);
        Assert.Equal(2.0, merged.Color.StrokeWidth);
        Assert.Equal("#FFFF00", merged.Color.HoverFill);
        Assert.Equal("#000000", merged.Color.HoverLabel);
    }

    [Fact]
    public void Merge_KeepsBaselineForUntouchedFields()
    {
        var baseline = DesignTokens.Parse(BaseJson);
        var merged = MergeAndParse("contrast");

        // 只覆盖了颜色与动效：几何与排版必须原样保留（否则就是"皮肤偷偷改了别的东西"）。
        Assert.Equal(baseline.Radius, merged.Radius);
        Assert.Equal(baseline.Typography, merged.Typography);
        Assert.Equal(baseline.Interaction, merged.Interaction);
    }

    /// <summary>
    /// 窗口尺寸是硬禁区：改它会让分层窗口重建整块表面，那正是报过三次闪烁的那类操作。
    /// 这条断言用一个**故意写错**的覆盖来证明守卫真的在起作用（否则它只是句注释）。
    /// </summary>
    [Fact]
    public void Merge_IgnoresWindowSizeOverride()
    {
        var baseline = DesignTokens.Parse(BaseJson);
        const string hostile = """
        {
          "version": 1,
          "skins": [
            { "id": "evil", "name": "越权皮肤",
              "overrides": { "radius": { "windowSize": 1200, "ring1Outer": 210 } } }
          ]
        }
        """;

        var catalog = SkinCatalog.Parse(hostile);
        var merged = DesignTokens.Parse(catalog.Merge(BaseJson, "evil"));

        Assert.Equal(baseline.Radius.WindowSize, merged.Radius.WindowSize);
        // 同一节里的其它字段照常生效 —— 守卫是"精确到 windowSize"，不是"整节拒绝"。
        Assert.Equal(210, merged.Radius.Ring1Outer);
    }

    /// <summary>行为类字段不给皮肤碰：同一个动作在不同皮肤下手感不同，那是 bug。</summary>
    [Fact]
    public void Merge_IgnoresInteractionSection()
    {
        var baseline = DesignTokens.Parse(BaseJson);
        const string hostile = """
        {
          "version": 1,
          "skins": [
            { "id": "evil2", "name": "想改行为的皮肤",
              "overrides": { "interaction": { "longPressMs": 200, "hoverOpenDelayMs": 0 } } }
          ]
        }
        """;

        var catalog = SkinCatalog.Parse(hostile);
        var merged = DesignTokens.Parse(catalog.Merge(BaseJson, "evil2"));

        Assert.Equal(baseline.Interaction, merged.Interaction);
    }


    [Fact]
    public void Merge_UnknownOrEmptySkin_保持基线并回填派生()
    {
        // 回填语义（spec R3）：合并结果不再与 tokens.json 逐字节相同（死区/角标变成派生值），
        // 这里锁「未覆盖节保持基线 + 次要面回填为确定派生值」—— 判据从字节相等升级为语义相等，不是放宽。
        var baseline = DesignTokens.Parse(BaseJson);
        foreach (var id in new[] { null, "", "does-not-exist" })
        {
            var merged = DesignTokens.Parse(Catalog.Merge(BaseJson, id));
            Assert.Equal(baseline.Radius, merged.Radius);
            Assert.Equal(baseline.Interaction, merged.Interaction);
            Assert.Equal(baseline.Color.Label, merged.Color.Label);
            Assert.Equal("#151515", merged.Color.DeadZoneFill);
            Assert.Equal("#0E0E0E", merged.Color.BadgeFill);
        }
        Assert.False(Catalog.Contains("does-not-exist"));
    }

    /// <summary>
    /// JSON 里 "_" 开头的字段是说明文字，不能被合并进令牌。
    ///
    /// 注意判据不能写成"合并后没有 _ 字段" —— 基线 tokens.json 自己就带一堆 _comment，
    /// 它们当然会留在合并结果里。要判的是**皮肤那条**说明没被搬进去。
    /// </summary>
    [Fact]
    public void Merge_SkipsCommentFields()
    {
        var merged = Catalog.Merge(BaseJson, "neon");

        // 霓虹档 radius 覆盖里那句说明的片段。
        Assert.DoesNotContain("鼓出 16px 需要子环内缘让出空间", merged);
        // 但同一节里真正要覆盖的值必须在（守卫不能把整节都吞掉）。
        Assert.Equal(220, DesignTokens.Parse(merged).Radius.Ring2Inner);
    }

    /// <summary>重复 id 会让"用户选了哪一套"变得不确定，必须在加载时就炸掉。</summary>
    [Fact]
    public void Parse_RejectsDuplicateIds()
    {
        const string dupes = """
        {
          "version": 1,
          "skins": [
            { "id": "same", "name": "甲", "overrides": {} },
            { "id": "same", "name": "乙", "overrides": {} }
          ]
        }
        """;

        Assert.Throws<InvalidOperationException>(() => SkinCatalog.Parse(dupes));
    }

    [Fact]
    public void Parse_RejectsEmptyCatalog()
    {
        Assert.Throws<InvalidOperationException>(() => SkinCatalog.Parse("""{ "version": 1, "skins": [] }"""));
    }

    /// <summary>每套皮肤都要有名字与出处 —— 面板上要显示，漏了用户就只看到一个 id。</summary>
    [Fact]
    public void EverySkin_HasNameAndSource()
    {
        foreach (var skin in Catalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(skin.Name), $"{skin.Id} 缺 name");
            Assert.False(string.IsNullOrWhiteSpace(skin.Tagline), $"{skin.Id} 缺 tagline");
            Assert.False(string.IsNullOrWhiteSpace(skin.Src), $"{skin.Id} 缺 src（出处）");
        }
    }
}
