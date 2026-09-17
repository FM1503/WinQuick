using WinQuick.Core.Design;

namespace WinQuick.Core.Motion;

/// <summary>一次唤出里的动作。**顺序本身是规格** —— 顺序错了就会出真实缺陷。</summary>
public enum SummonStep
{
    /// <summary>清掉休眠态：缩放/角度归位、内容恢复、不透明度准备。</summary>
    LeaveDormancy,

    /// <summary>落定"显示之前"的状态：动画开启时是入场起点，关闭时是终态。</summary>
    SettlePreShowState,

    /// <summary>摆位并让窗口出现在屏幕上（此刻屏幕上显示的东西必须是上一步的结果）。</summary>
    ShowAtCursor,

    /// <summary>整栈一次性重申 z 序（逐窗重申会在中间态被合成器取走一帧）。</summary>
    ReassertTopmost,

    /// <summary>播放入场动画（仅动画开启时）。</summary>
    PlayEntrance,
}

/// <summary>
/// 唤出序列。
///
/// 为什么把它单独抽出来当规格：这个项目出过的两类真实缺陷**根因都是顺序**——
/// 1. 先把窗口挪到屏上、再落定入场起始状态 → 用户看到"先闪一帧完整圆盘"；
/// 2. 休眠时清空了内容、却没在唤出前恢复 → 第二次唤出是个空轮盘。
/// 顺序这种东西写在 App 的方法体里没人能复核，写成 Core 的列表就能被单测锁住。
/// </summary>
public static class SummonSequence
{
    /// <param name="animationsEnabled">系统是否允许播放动画（无障碍开关）。</param>
    public static IReadOnlyList<SummonStep> Steps(bool animationsEnabled) =>
        animationsEnabled
            ?
            [
                SummonStep.LeaveDormancy,
                SummonStep.SettlePreShowState,
                SummonStep.ShowAtCursor,
                SummonStep.ReassertTopmost,
                SummonStep.PlayEntrance,
            ]
            :
            [
                SummonStep.LeaveDormancy,
                SummonStep.SettlePreShowState,
                SummonStep.ShowAtCursor,
                SummonStep.ReassertTopmost,
            ];
}

/// <summary>一次退场里的动作。**退场不得阻塞动作执行**（Kando #650 的教训）。</summary>
public enum DismissStep
{
    /// <summary>焦点立刻归还用户原来的窗口 —— 不等动画。</summary>
    ReturnFocusImmediately,

    /// <summary>遮罩立刻消失（它是用户的视觉反馈）。</summary>
    HideDimImmediately,

    /// <summary>起播退场动画。</summary>
    StartExitAnimation,

    /// <summary>等退场动画走完（仅动画开启时）。</summary>
    WaitExitAnimation,

    /// <summary>进入休眠态：停放 + 归位 + **不透明度保持 0**。</summary>
    EnterDormancy,
}

/// <summary>退场序列。同样把顺序当规格：焦点与遮罩必须在"等动画"之前。</summary>
public static class DismissSequence
{
    public static IReadOnlyList<DismissStep> Steps(bool animationsEnabled) =>
        animationsEnabled
            ?
            [
                DismissStep.ReturnFocusImmediately,
                DismissStep.HideDimImmediately,
                DismissStep.StartExitAnimation,
                DismissStep.WaitExitAnimation,
                DismissStep.EnterDormancy,
            ]
            :
            [
                DismissStep.ReturnFocusImmediately,
                DismissStep.HideDimImmediately,
                DismissStep.EnterDormancy,
            ];
}

/// <summary>入场时真正被动画的一条通道。</summary>
public readonly record struct MotionChannel(string Name, double From, double To, int DurationMs, string Easing);

/// <summary>
/// 入场动画的计划：每条通道的起点、终点、时长、曲线。
///
/// 抽出来的价值在于**终点必须精确落在终态**这件事可以被单测锁住 ——
/// 入场动画留下的"没归位"在分层窗口上表现为永久歪一点/淡一点，
/// 而且只在之后某次操作时才暴露（本项目为此吃过两次亏：缩放与角度各一次）。
/// </summary>
public static class EntrancePlan
{
    public const string ScaleChannel = "scale";
    public const string RotateChannel = "rotate";
    public const string FadeChannel = "fade";

    /// <summary>窗口在"停放（休眠）"与"入场起点"两个状态下共享的不透明度：都是 0。
    /// 这两个值相同是刻意的 —— 挪回屏幕的那一瞬不可能露出上一轮的内容。</summary>
    public const double PreShowOpacity = 0.0;

    public static IReadOnlyList<MotionChannel> Channels(MotionTokens motion, bool animationsEnabled)
    {
        if (!animationsEnabled) return [];

        var channels = new List<MotionChannel>
        {
            new(ScaleChannel, motion.EntranceFromScale, 1.0, motion.EntranceScaleMs, motion.EntranceScaleEasing),
            new(FadeChannel, PreShowOpacity, 1.0, motion.EntranceDurationMs, motion.EntranceEasing),
        };

        // 起始角为 0 时不产生旋转通道：省一次无意义的动画订阅。
        if (Math.Abs(motion.EntranceFromRotateDeg) > 0.01)
        {
            channels.Add(
                new(RotateChannel, motion.EntranceFromRotateDeg, 0.0, motion.EntranceRotateMs, motion.EntranceRotateEasing));
        }

        return channels;
    }
}
