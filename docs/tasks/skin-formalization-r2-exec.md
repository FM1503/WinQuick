# 执行书 R2：对比度校验配对 + 动效过冲规则 + SkinContrastTests

## S1 DesignTokens.cs：Validate 补两对配对
先 Read 定位「悬停时文字与填充的对比度」段（hoverFill 配对块之后）。追加：
```csharp
        // 皮肤对比度全配对（spec skin-formalization §6）：浅色皮肤暴露出「只查 hover 一对」的缺口 ——
        // 正文/次级文字贴着扇区底，任一配对亮度太接近都是「内容读不出来」。
        // badge（角标）经核实为纯色块无文字（RadialRingVisual.cs:981 仅 Fill），无文字配对可查，如实豁免。
        if (ContrastIsLow(Color.SectorFill, Color.Label))
            problems.Add(
                $"color.sectorFill（{Color.SectorFill}）与 color.label（{Color.Label}）亮度太接近 —— 正文读不出来");
        if (ContrastIsLow(Color.SectorFill, Color.Hint))
            problems.Add(
                $"color.sectorFill（{Color.SectorFill}）与 color.hint（{Color.Hint}）亮度太接近 —— 次级文字读不出来");
```

## S2 DesignTokens.cs：easings 表后补过冲上限规则
先 Read 定位 easings 表（六条曲线校验）之后的位置。追加：
```csharp
        // 动效过冲上限（M3 规矩规则化，spec §3）：可见过冲是 M3 expressive 的官方档位（damping≈0.8），
        // 但幅度有上限 —— 贝塞尔控制点 y2 超过 1.25 就是「弹过头」，观感即「糙」。
        // 过冲只应出现在 transform 类通道（Scale/Rotate/Bulge/Slide）；entranceEasing/exitEasing
        // 若驱动透明度必须临界阻尼 —— 现状各皮肤均满足，此规则防未来越线。
        foreach (var (name, value) in easings)
        {
            if (MotionTokens.TryParseEasing(value, out var p) && (p.Y1 > 1.25 || p.Y2 > 1.25))
                problems.Add($"motion.{name}（{value}）过冲超出上限（控制点 y ≤ 1.25）—— M3 expressive 的可见过冲档不含这种幅度");
        }
```
（MotionTokens.TryParseEasing 的可访问性与返回形状先 grep 核实；若 (p.X1,p.Y1,p.X2,p.Y2) 形状不同按现场适配；easings 变量名以现场为准。）

## S3 新建 tests/WinQuick.Core.Tests/SkinContrastTests.cs（全文）
```csharp
using WinQuick.App.Design;
using WinQuick.Core.Design;

namespace WinQuick.Core.Tests;

/// <summary>
/// 皮肤可读性回归锁（spec skin-formalization §6）：每套皮肤合并后的令牌必须整体通过
/// DesignTokens.Validate（含新增的正文/次级文字对比度配对与过冲上限）。
/// 浅色皮肤的「内容显示问题」就是只查 hover 一对漏出来的 —— 这组测试保证任何一套皮肤
/// 改坏了配色都会在单测层红掉，而不是等用户在浅色下看见深色块。
/// 对照用例保证校验本身能失败（拦得住），不是恒真。
/// </summary>
public class SkinContrastTests
{
    public static IEnumerable<object[]> AllSkins()
    {
        foreach (var skin in TokenStore.Skins.All)
            yield return new object[] { skin.Id };
    }

    [Theory]
    [MemberData(nameof(AllSkins))]
    public void 每套皮肤合并令牌整体通过校验(string skinId)
    {
        var merged = TokenStore.PreviewTokens(skinId);
        var problems = merged.Validate();
        Assert.True(problems.Count == 0,
            $"皮肤「{skinId}」未过校验：{string.Join("；", problems)}");
    }

    [Fact]
    public void 校验能拦住低对比组合_对照()
    {
        // 白底白字：如果配对校验被删或阈值被放宽，这条会红 —— 保证上面的 Theory 不是恒真。
        var tokens = TokenStore.PreviewTokens(TokenStore.Skins.All[0].Id);
        var bad = tokens with { Color = tokens.Color with { Label = tokens.Color.SectorFill } };
        Assert.Contains(bad.Validate(), p => p.Contains("label"));
    }
}
```
（`with` 表达式要求 record 类型——ColorTokens/DesignTokens 均 record（已核实 ColorTokens 为 sealed record）；若 DesignTokens 非 record 或嵌套 with 不可行，改用最小构造路径并保持断言语义。TokenStore.PreviewTokens/Skins 的可访问性若不足 internal，补 InternalsVisibleTo 已有 ✔。）

## S4 spec §6 配对表如实修正
docs/specs/skin-formalization.md §6 中「badge 文字↔badgeFill」半句替换为「badge 为纯色块无文字（已核实），无文字配对可查」。

## 验证
1. `dotnet build src/WinQuick.Core/WinQuick.Core.csproj -c Release --nologo` → 0 error；
2. `dotnet test tests/WinQuick.Core.Tests --filter SkinContrast --nologo` → 全绿（7 套 Theory+1 对照）；
3. `dotnet test tests/WinQuick.Core.Tests --nologo` → 全量不回退（321+8）。
