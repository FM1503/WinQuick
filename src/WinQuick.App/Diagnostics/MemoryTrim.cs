using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.System.Memory;

namespace WinQuick.App.Diagnostics;

/// <summary>
/// 空闲时把进程的工作集交还给系统。
///
/// ## 为什么需要它（用户要求）
///
/// 这个工具是**常驻**的：轮盘收起来之后窗口只是被停放、不销毁，那套 WPF 渲染栈与
/// 分层窗口的表面仍然留在进程的工作集里（实测静置约 **140MB 工作集**）。
/// 用户用内存清理工具（MEM REDUCT）清一次之后，未唤出时只有 **1.4MB** ——
/// 那正是这条机制：**把工作集换出**（页进 standby 列表），而不是"释放内存"。
/// 对一个常驻托盘工具来说，空闲时不占着工作集是合理目标。
///
/// ## 它做了什么、没做什么（必须说清）
///
/// * **做**：让系统把本进程的页换到 standby 列表 → 任务管理器的"内存"列大幅下降。
/// * **没做**：**提交量（commit）不变**。那部分内存仍然是本进程保留的，只是不再驻留在物理内存里；
///   系统需要时可以直接回收（standby 页是"缓存"而非法"已用"）。
/// * **代价**：下次唤出要重新缺页（soft fault）。页在 standby 里，**通常不触发磁盘读**，
///   但首次唤出的那几帧会慢一点 —— 所以调用点是"关闭后**延迟** N 秒"，
///   快速再唤出（常见情形）不会碰上它。
///
/// ## 顺序为什么重要
///
/// 先让托管垃圾与终结器跑完（WPF 的非托管渲染数据挂在终结器上），
/// 再交给系统换出工作集 —— 反过来的话，刚被换出的页会因为 GC 触碰对象又立刻回来。
/// </summary>
internal static class MemoryTrim
{
    /// <summary>压缩回收的次数（诊断用）。</summary>
    public static int TrimCount { get; private set; }

    /// <summary>上一次清理前的工作集（字节），便于核对"到底降了多少"。</summary>
    public static long LastWorkingSetBefore { get; private set; }

    /// <summary>上一次清理后的工作集（字节）。</summary>
    public static long LastWorkingSetAfter { get; private set; }

    /// <summary>
    /// 跑一次：强制回收（含终结器与压缩），然后把工作集交给系统换出。
    /// 返回"清理前后的工作集"（拿不到进程信息时返回 null）。
    /// </summary>
    public static (long Before, long After)? Run()
    {
        long before = 0, after = 0;
        try
        {
            using (var process = Process.GetCurrentProcess())
                before = process.WorkingSet64;

            // ① 托管垃圾 + 终结器（WPF 的渲染数据在终结器里释放）
            for (var i = 0; i < 2; i++)
            {
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            // ② 把工作集交给系统换出。
            //
            // 传 (-1, -1) 是官方语义里的"尽量换出"（与那些内存清理工具同一个调用）。
            // 失败不影响功能：这只是省内存，省不到就算了。
            PInvoke.SetProcessWorkingSetSizeEx(
                PInvoke.GetCurrentProcess(),
                unchecked((nuint)(-1)),
                unchecked((nuint)(-1)),
                0);

            using (var process = Process.GetCurrentProcess())
                after = process.WorkingSet64;

            TrimCount++;
            LastWorkingSetBefore = before;
            LastWorkingSetAfter = after;
            return (before, after);
        }
        catch (Exception ex)
        {
            CrashLog.Write("空闲内存清理失败", ex);
            return null;
        }
    }
}
