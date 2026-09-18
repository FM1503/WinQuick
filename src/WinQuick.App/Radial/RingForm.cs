namespace WinQuick.App.Radial;

/// <summary>
/// 盘面形态。**与皮肤正交的另一个维度**：皮肤换材质与动效，形态换单元的几何外形。
///
/// 为什么形态是"一组几何生成器"而不是皮肤里的几个数值：
/// 换了外形，命中测试、标签预算、可读性判据都会跟着变 —— 那是策略而不是参数。
/// 所以形态由代码实现（这里是纯函数），皮肤/配置只能**选一个 id**。
/// 用户自己的皮肤文件永远只是数据，不可能带代码进来。
///
/// 本批只做**沿用同一个环形扇区占位**的四种 —— 圆角、花瓣、刻度盘都落在
/// 原有扇区的角度区间里，所以命中测试与既有的几何不变量原样成立。
/// 卡片弧与网格（把单元换成矩形、把环换成格子）需要重写命中测试与整套校验规则，
/// 不在本批（见 docs/M8-skins.md §11）。
/// </summary>
public enum RingForm
{
    /// <summary>尖角楔形（现状）。</summary>
    Pie,

    /// <summary>圆角楔形：同样占位，四个角改成圆弧，Fluent 的卡片感。</summary>
    Rounded,

    /// <summary>花瓣：内侧收成尖、外侧是宽圆弧，片间留大空隙。</summary>
    Petal,

    /// <summary>刻度盘：楔形让出外圈，外圈画 60 条刻度并有一根指向悬停项的指针。</summary>
    Dial,
}

/// <summary>形态的 id、显示名与几何参数。</summary>
public static class RingForms
{
    /// <summary>按 id 解析；未知/空 → <see cref="RingForm.Pie"/>（现状，永远安全）。</summary>
    public static RingForm Parse(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "rounded" => RingForm.Rounded,
        "petal" => RingForm.Petal,
        "dial" => RingForm.Dial,
        _ => RingForm.Pie,
    };

    public static string IdOf(RingForm form) => form switch
    {
        RingForm.Rounded => "rounded",
        RingForm.Petal => "petal",
        RingForm.Dial => "dial",
        _ => "pie",
    };

    public static string NameOf(RingForm form) => form switch
    {
        RingForm.Rounded => "圆角楔形",
        RingForm.Petal => "花瓣",
        RingForm.Dial => "刻度盘",
        _ => "饼形扇区",
    };

    public static string TaglineOf(RingForm form) => form switch
    {
        RingForm.Rounded => "Fluent 卡片感，四角圆润",
        RingForm.Petal => "八片分离花瓣，片间留空",
        RingForm.Dial => "外圈 60 刻度 + 指针指向悬停项",
        _ => "现状：尖角楔形",
    };

    /// <summary>
    /// 形态对环的参数修正。
    /// · 花瓣的内缘比死区再退 12px，且**缝隙要拉大**：花瓣的立身之本是"片片分离"，
    ///   沿用饼形那 1.2° 的缝就变成"一整个环带抠了几个尖角"，与原型里那一档完全不是一回事
    ///   （这条不一致是被 --render-preview + analyze-render 的角向扫描量出来的：
    ///   改之前花瓣在 r=150 处的角宽 42°、与饼形几乎相同）；
    /// · 刻度盘把楔形的外缘收到 0.88：外圈那 12% 让给刻度与指针。
    /// </summary>
    public static (double Inner, double Outer, double CornerRadius, double GapDegrees) Metrics(
        RingForm form, double deadZone, double ring1Outer, double baseGap) => form switch
    {
        RingForm.Rounded => (deadZone, ring1Outer, 14, baseGap),
        RingForm.Petal => (deadZone + 12, ring1Outer, 0, Math.Max(baseGap, 6.0)),
        RingForm.Dial => (deadZone, ring1Outer * 0.88, 0, baseGap),
        _ => (deadZone, ring1Outer, 0, baseGap),
    };

    /// <summary>刻度盘的刻度数量（每 6° 一条）。自检会核对这个数。</summary>
    public const int DialTickCount = 60;
}
