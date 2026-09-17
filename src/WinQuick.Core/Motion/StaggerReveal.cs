namespace WinQuick.Core.Motion;

/// <summary>
/// 逐项依次出现的节奏 —— 把"第 i 项什么时候开始、整轮多久"算清楚。
///
/// 为什么单独抽成纯函数：这项数一变（8 项 / 12 项 / 20 个子项）参数就得跟着变，
/// 而"顺便压一下交错"这种判断最容易写错又不报错 —— 错了的样子是
/// "项多的时候整轮拖到一秒以上，用户读成卡顿"。放在这里就能用单测锁住。
/// </summary>
public static class StaggerReveal
{
    /// <summary>
    /// 求交错间隔与整轮时长。
    ///
    /// 规则：交错按给定值，但**整轮不超过 <paramref name="sweepMaxMs"/>** ——
    /// 项数多时自动压缩交错。上限的存在是因为"依次出现"的观感强依赖总时长：
    /// 8 项 × 55ms 是舒服的，20 项 × 55ms 就是一秒多，会被读成卡顿。
    /// </summary>
    /// <param name="count">项数。</param>
    /// <param name="staggerMs">希望的相邻两项起始间隔。</param>
    /// <param name="itemMs">单项自己的时长（从开始到落定）。</param>
    /// <param name="sweepMaxMs">整轮时长上限。</param>
    public static (double StaggerMs, double TotalMs) Plan(
        int count, double staggerMs, double itemMs, double sweepMaxMs)
    {
        itemMs = Math.Max(1, itemMs);

        if (count <= 1) return (0, itemMs);

        var stagger = Math.Max(0, staggerMs);
        var budget = Math.Max(0, sweepMaxMs - itemMs);

        if (stagger * (count - 1) > budget) stagger = budget / (count - 1);

        return (stagger, stagger * (count - 1) + itemMs);
    }

    /// <summary>
    /// 把动画器的原始进度翻成**实际画出来的完整度**（"残影首态"）。
    ///
    /// 原始进度 0（＝刚轮到它）不是"什么都没有"，而是首态：由 <paramref name="ghost"/> 给定的
    /// 那一份厚度/不透明度；原始进度 1 才是完整实态。好处是每格不必从零长起 ——
    /// 它一出现就已经"存在"，于是格距可以压得很小、整轮短得多，而"新出现一格"
    /// 仍是可读的离散事件。
    ///
    /// 原始进度小于 0（＝还没轮到）才是不显示。<paramref name="ghost"/> 为 0 时退化成旧版。
    ///
    /// 抽成纯函数是为了能被单测锁住：观感类的东西没法自动判，但这条映射可以 ——
    /// 而它一旦写错（比如把 0 也映射成"不显示"），残影就形同虚设、且肉眼不一定看得出来。
    /// </summary>
    public static double ApplyGhost(double raw, double ghost)
    {
        if (raw < 0) return 0;

        ghost = Math.Clamp(ghost, 0, 0.9);
        return ghost + (1 - ghost) * Math.Clamp(raw, 0, 1);
    }

    /// <summary>第 <paramref name="index"/> 项在 <paramref name="elapsedMs"/> 时刻的线性进度（0..1，未加缓动）。</summary>
    public static double RawProgress(int index, double elapsedMs, double staggerMs, double itemMs)
    {
        if (index < 0) return 1;
        itemMs = Math.Max(1, itemMs);

        var local = (elapsedMs - index * Math.Max(0, staggerMs)) / itemMs;
        return Math.Clamp(local, 0, 1);
    }
}
