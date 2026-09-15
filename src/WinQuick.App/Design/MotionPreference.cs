using System.Windows;

namespace WinQuick.App.Design;

/// <summary>
/// 「要不要播动画」这个偏好。
///
/// 之前这个判断在三个地方各写了一遍（轮盘窗口、覆盖层控制器、探针），
/// 现在又要给设置面板用 —— 第四份重复说明该收成一处了。
///
/// 优先用用户在本工具里的显式设置（<c>behavior.animationsEnabled</c>，
/// null 表示跟随系统）；跟随系统时读的是「在 Windows 中显示动画」这个无障碍开关 ——
/// 这类偏好必须尊重：关掉动画的用户里有相当一部分是因为动画会引起不适，
/// 而不是"觉得不好看"。
/// </summary>
internal static class MotionPreference
{
    /// <summary>系统层面是否允许动画。读不到时按"允许"处理（失败不该导致界面不动）。</summary>
    public static bool SystemAllows
    {
        get
        {
            try { return SystemParameters.ClientAreaAnimation; }
            catch { return true; }
        }
    }

    /// <summary>用户设置覆盖；null 表示跟随系统。</summary>
    public static bool IsEnabled(bool? userOverride) => userOverride ?? SystemAllows;
}
