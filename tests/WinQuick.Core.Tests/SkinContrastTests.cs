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
