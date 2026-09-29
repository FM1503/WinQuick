namespace WinQuick.Core.Interaction;

/// <summary>
/// 轮盘中心（死区）的分区规则。
///
/// 中心分成两半：**左半 = Esc（关闭整棵轮盘），右半 = 返回上一层**（用户 2026-09 指定的分工）。
/// 分区不破坏命中模型：死区本来就是「不选中任何项」，这里只是给这块空白补两种语义 ——
/// 命中测试一行都不用改，只有"落到死区之后做什么"在这里定。
///
/// 角度约定沿用整个项目的那一套：0° = 正上，顺时针（90° = 右，180° = 下，270° = 左）。
///
/// ## 这里踩过的坑（写下来防重蹈）
///
/// 第一版把「左半」写成了 <c>angle &gt;= 270 || angle &lt; 90</c> —— 那个集合是
/// **上/下两半**（9 点→12 点→3 点），不是左右。屏上画的是竖中缝 + 左右两列字，
/// 于是标签与动作对不上：点"左列"的下半段触发的是"上一层"。
/// 单测还把这个错固化成了预期，探针又只采样了 3 点钟与 9 点钟两个
/// **恰好在两种划分下结论一致**的边界点 —— 全绿通过，全是假象。
/// 所以现在这条判定只有一行，且测试与探针都必须打**内部点**，不打边界。
/// </summary>
public static class CenterZones
{
    /// <summary>
    /// 这一半是不是「Esc（关闭）」侧 —— 即左半。
    /// 左半 = 指针在中心左边的所有角度 (180°, 360°)；正上 0° 与正下 180° 归右半（边界要有确定归属）。
    /// </summary>
    public static bool IsEscHalf(double angleDeg)
    {
        var angle = Normalize(angleDeg);
        return angle > 180.0;
    }

    /// <summary>
    /// 「返回上一层」可用吗 —— 深于第一层才有意义；第一层时界面上把它画灰、点了不动。
    /// </summary>
    public static bool IsBackAvailable(int depth) => depth >= 1;

    /// <summary>把角度归一化到 [0,360)。</summary>
    public static double Normalize(double angleDeg)
    {
        var angle = angleDeg % 360.0;
        return angle < 0 ? angle + 360.0 : angle;
    }
}
