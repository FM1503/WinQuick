using System.Windows;
using System.Windows.Interop;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;

namespace WinQuick.App.M0;

/// <summary>
/// 虚拟桌面探针 —— 核对"覆盖层的窗口会不会跟着用户走"这件事的**接线部分**。
///
/// ## 它验什么、不验什么
///
/// 验：COM 接线对不对（能不能拿到 `IVirtualDesktopManager`、能不能读到窗口的桌面 id、
/// "已经在当前桌面"时是否一次窗口操作都不发）；以及那条**能失败的对照**：
/// 如果接线是坏的（比如 vtable 错位、每次都答"不在当前桌面"），
/// 搬动计数就会 &gt; 0，这里立刻报红。
///
/// 不验：跨桌面的真实行为（"在桌面 B 按热键，轮盘是否出现在 B"）——
/// 那需要程序化切换虚拟桌面，而切换只能靠未文档化的私有接口
/// （`IVirtualDesktopManagerInternal`，每版 Windows 的 vtable 都变）。
/// 一个探针不值得为此把整机稳定性押上去，所以**这一条由用户手动验**。
///
/// ## 为什么自己建一个窗口
///
/// 读桌面 id 需要一个真实 HWND。<see cref="WindowInteropHelper.EnsureHandle"/> 只创建句柄、
/// **不显示窗口** —— 于是这个探针全程不会在用户屏幕上出现任何东西。
/// </summary>
public static class VirtualDesktopProbe
{
    public static void Run()
    {
        ProbeOutput.Initialize("virtual-desktop", "m8");

        var window = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
        };

        // 只建句柄，不显示：句柄一建立就固定属于"创建时所在的那个桌面"，
        // 这正是本探针要观察的那个事实本身。
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        ProbeOutput.Section("接线");
        ProbeOutput.Kv("拿到 IVirtualDesktopManager", VirtualDesktop.IsSupported ? "是" : "否（下面各项会退化为空操作）");
        var onCurrent = VirtualDesktop.IsOnCurrentDesktop(hwnd);
        ProbeOutput.Kv("新建窗口在不在当前桌面", onCurrent ? "在（预期）" : "不在（异常）");
        ProbeOutput.Kv("当前桌面 id 的来源", VirtualDesktop.LastIdSource);
        foreach (var line in VirtualDesktop.IdDiagnostics) ProbeOutput.Line("    " + line);

        ProbeOutput.Section("搬动逻辑");
        var movedBefore = VirtualDesktop.MovedCount;
        var skippedBefore = VirtualDesktop.SkippedCount;

        // ① 已经在当前桌面：应当一次都不搬。
        var moved = VirtualDesktop.MoveAllToCurrentDesktop([hwnd]);
        var skipped = VirtualDesktop.SkippedCount - skippedBefore;
        ProbeOutput.Kv("把已在本桌面的窗口搬一次", moved == 0 ? "0 个（预期：不发窗口操作）" : $"{moved} 个（异常）");
        ProbeOutput.Kv("走的是「已在当前桌面」分支", skipped >= 1 ? $"是（跳过 {skipped} 次）" : "否（异常）");

        // ② 句柄为 0：必须被跳过，不能被算成"搬动"。
        var movedForInvalid = VirtualDesktop.MoveAllToCurrentDesktop([0]);
        ProbeOutput.Kv("句柄为 0 时", movedForInvalid == 0 ? "跳过（预期）" : $"{movedForInvalid} 个（异常）");

        ProbeOutput.Section("结论");
        var problems = new List<string>();
        if (!VirtualDesktop.IsSupported)
            problems.Add("拿不到 IVirtualDesktopManager —— 换桌面后唤出会退回原来那两种现象");
        if (!onCurrent)
            problems.Add("新建窗口竟不在当前桌面：桌面 id 读取有问题");
        if (moved != 0) problems.Add($"已在本桌面的窗口被搬动了 {moved} 次：接线可能错位");
        if (skipped < 1) problems.Add("没走「已在当前桌面」分支：判据可能恒为假");
        if (movedForInvalid != 0) problems.Add("句柄为 0 时被算成一次搬动");
        if (VirtualDesktop.FailedCount > 0)
            problems.Add($"有 {VirtualDesktop.FailedCount} 次搬动失败（复验仍不在当前桌面）");
        if (VirtualDesktop.LastIdSource == "取不到")
            problems.Add("取不到当前桌面 id —— 搬动会静默不发生，用户在别的桌面按热键时还是老毛病");

        if (problems.Count == 0)
        {
            ProbeOutput.Line("  接线正确：读得到桌面、已在本桌面时不发窗口操作、非法句柄被跳过。");
            ProbeOutput.Line("  ⚠ 跨桌面的真实行为需要手动验：在桌面 A 唤出后切到桌面 B，再按热键，");
            ProbeOutput.Line("     轮盘应当出现在 B（且不会把用户拽回 A）。");
        }
        else
        {
            foreach (var problem in problems) ProbeOutput.Line("  ✗ " + problem);
        }

        ProbeOutput.Section("汇总");
        ProbeOutput.Kv("拿得到管理器", VirtualDesktop.IsSupported ? "是" : "否");
        ProbeOutput.Kv("新建窗口判定", onCurrent ? "在当前桌面" : "不在当前桌面（异常）");
        ProbeOutput.Kv("已在本桌面时不搬动", moved == 0 ? "通过" : "失败");
        ProbeOutput.Kv("非法句柄被跳过", movedForInvalid == 0 ? "通过" : "失败");
        ProbeOutput.Kv("搬动计数（累计）", $"{movedBefore} → {VirtualDesktop.MovedCount}");
        ProbeOutput.Kv("搬动失败次数", VirtualDesktop.FailedCount);
        ProbeOutput.Kv("当前桌面 id 的来源", VirtualDesktop.LastIdSource);
        foreach (var line in VirtualDesktop.IdDiagnostics) ProbeOutput.Line("    " + line);
        ProbeOutput.Section("端到端自证（一次性窗口搬来搬去；用户看不见）");
        foreach (var line in VirtualDesktop.SelfTest()) ProbeOutput.Line("  " + line);

        ProbeOutput.Section("接口三个方法的原始返回码（判定槽位是否错位）");
        foreach (var line in VirtualDesktop.DiagnoseMethods(hwnd)) ProbeOutput.Kv("  " + line.Split(" → ")[0], line.Split(" → ")[1]);
        ProbeOutput.Kv("总体", problems.Count == 0 ? "全部通过" : "存在失败");

        foreach (var problem in problems) ProbeOutput.Line("  ✗ " + problem);

        ProbeOutput.Flush();
        window.Close();
    }
}
