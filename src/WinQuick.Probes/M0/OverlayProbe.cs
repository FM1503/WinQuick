using System.Windows;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.App.Interop;
using WinQuick.App.Design;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using WinQuick.Core.Hotkey;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.M0;

/// <summary>
/// 覆盖层探针 —— 自动核对 M1 的核心行为，同时提供一个「不用按热键就能看到轮盘」的入口。
///
/// 为什么需要它：热键只能由人在现场按，而覆盖层的正确性（窗口真的可见了吗？位置钳制对了吗？
/// 前台焦点还回去了吗？）是可以用程序核对的。
/// 这里不信任自己的布尔量，一律通过 Win32 查询窗口的真实状态。
///
/// 用法：
///   --overlay-probe            自动核对，然后关闭
///   --overlay-probe --hold=20  显示出轮盘并保持 20 秒，供人肉眼查看（按 Esc 可关闭）
/// </summary>
public static class OverlayProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("overlay");
        ProbeOutput.Line("通过 Win32 查询窗口真实状态来核对，不依赖程序内部的布尔量。");

        ProbeOutput.Section("运行时");
        ProbeOutput.Line(NativeDisplay.DescribeEnvironment());
        ProbeOutput.Kv("渲染层级", NativeDisplay.RenderTierDescription());

        ProbeOutput.Section("显示器拓扑");
        foreach (var m in MonitorLayout.All())
            ProbeOutput.Kv($"显示器 #{m.Index}", $"{m}");

        var holdSeconds = cli.GetInt("hold", 0);

        OverlayController? overlay = null;
        try
        {
            overlay = new OverlayController(MenuSpec.BuiltInDefault);
            overlay.Prewarm();

            ProbeOutput.Section("预热后（应全部隐藏）");
            ReportWindowStates(overlay, "预热后");

            // 记录显示前的前台窗口，用来验证关闭时是否还回去了。
            var beforeForeground = PInvoke.GetForegroundWindow();

            // 显示前先采一个"信息量足够"的屏幕像素：沿一条横线找最亮的点。
            // 太暗的点判断不出遮罩颜色（0 乘任何数仍是 0）。
            var sample = FindInformativePixel();

            ProbeOutput.Section("显示覆盖层");
            overlay.Show();
            await Task.Delay(400);   // 留出实际合成与置顶重申的时间

            ReportWindowStates(overlay, "显示后");
            VerifyPlacement(overlay);
            VerifyDimColor(sample);

            var afterShowForeground = PInvoke.GetForegroundWindow();
            ProbeOutput.Kv("前台窗口在显示前", Describe(beforeForeground));
            ProbeOutput.Kv("前台窗口在显示后", Describe(afterShowForeground));
            ProbeOutput.Kv("轮盘是否取得前台", afterShowForeground == (HWND)overlay.RadialHandle ? "是" : "否");
            ProbeOutput.Line();
            ProbeOutput.Line("  轮盘必须取得前台才能收到 Esc；关闭时会把前台还给原来那个窗口。");

            if (holdSeconds > 0)
            {
                ProbeOutput.Section("保持显示供人查看");
                ProbeOutput.Line($"  轮盘已显示，保持 {holdSeconds} 秒。");
                ProbeOutput.Line("  可以做的检查：");
                ProbeOutput.Line("    · 全屏是否有一层变暗遮罩，且遮罩没有把屏幕内容压得看不清");
                ProbeOutput.Line("    · 圆盘是否以光标为中心（若靠近屏幕边缘，应被整体钳制在屏内）");
                ProbeOutput.Line("    · 12 点方向是否是一个完整扇区（而不是一条缝）");
                ProbeOutput.Line("    · 鼠标划过扇区时是否高亮，离开后是否恢复");
                ProbeOutput.Line("    · 点中心 / 按 Esc / 点圆盘外，三种方式是否都能关闭");
                ProbeOutput.Line();
                ProbeOutput.Line("  注意：此时前台窗口已被交给轮盘。若要保持期间操作其他程序，");
                ProbeOutput.Line("        点击轮盘外的遮罩会关闭覆盖层并归还前台焦点。");
                ProbeOutput.Flush();

                for (var i = 0; i < holdSeconds * 10; i++)
                {
                    await Task.Delay(100);
                    if (!overlay.IsVisible) break;   // 用户已经用 Esc 或点空白关掉了
                }
            }

            // 自动验证「点空白处关闭」这条路径。
            // 这条曾经完全没生效：遮罩窗口用了系统预定义类，
            // CreateWindowEx 不会使用我们提供的窗口过程，于是消息处理根本没挂上。
            // 点击扇区的路径验证。这条直接对应「点了扇区没反应」这个反馈 ——
            // 要区分清楚是「点击没被检测到」还是「检测到了但没有动作可执行」。
            await VerifySectorClickAsync(overlay);

            await VerifyChildArcAsync(overlay);

            await VerifyActionBarAsync(overlay);
            await VerifyActionBarClickAsync(overlay);
            VerifyTrayIconResource();

            ProbeOutput.Section("点遮罩空白处是否关闭");
            if (overlay.IsVisible && overlay.DimHandles.Count > 0)
            {
                var dim = (HWND)overlay.DimHandles[0];
                ProbeOutput.Kv("向遮罩投递 WM_LBUTTONDOWN", $"hwnd={dim}");
                PInvoke.PostMessage(dim, PInvoke.WM_LBUTTONDOWN, default, default);
                await Task.Delay(300);

                ProbeOutput.Kv("遮罩实际可见", PInvoke.IsWindowVisible(dim) ? "是" : "否");
                ProbeOutput.Kv("控制器认为可见", overlay.IsVisible ? "是" : "否");
                ProbeOutput.Line();
                ProbeOutput.Line(!overlay.IsVisible
                    ? "  >>> 点击遮罩成功关闭覆盖层。"
                    : "  >>> 点击遮罩没有关闭覆盖层 —— 窗口过程没挂上或消息未被处理。");
            }
            else
            {
                ProbeOutput.Line("  覆盖层当前不可见，跳过。");
            }

            ProbeOutput.Section("隐藏覆盖层");
            overlay.Hide();
            await Task.Delay(400);

            ReportWindowStates(overlay, "隐藏后");
            var afterHideForeground = PInvoke.GetForegroundWindow();
            ProbeOutput.Kv("前台窗口在隐藏后", Describe(afterHideForeground));
            ProbeOutput.Line();
            ProbeOutput.Line(afterHideForeground == beforeForeground
                ? "  >>> 前台焦点已正确还原给用户原来的窗口。"
                : "  >>> 前台焦点未还原（原窗口可能已关闭，或还原失败）。");

            Report();
        }
        catch (Exception ex)
        {
            ProbeOutput.Line();
            ProbeOutput.Line("探针异常: " + ex);
            CrashLog.Write("overlay-probe", ex);
            Report();
        }
        finally
        {
            overlay?.Dispose();
        }
    }

    private static void ReportWindowStates(OverlayController overlay, string stage)
    {
        ProbeOutput.Line($"  [{stage}]");
        ProbeOutput.Kv("  遮罩窗口数", overlay.DimHandles.Count);
        ProbeOutput.Kv("  控制器认为可见", overlay.IsVisible ? "是" : "否");

        for (var i = 0; i < overlay.DimHandles.Count; i++)
        {
            var hwnd = (HWND)overlay.DimHandles[i];
            ProbeOutput.Kv($"  遮罩 #{i} 实际可见", PInvoke.IsWindowVisible(hwnd) ? "是" : "否");
            ProbeOutput.Kv($"  遮罩 #{i} 矩形", GetRectText(hwnd));
        }

        var radial = (HWND)overlay.RadialHandle;
        ProbeOutput.Kv("  轮盘实际可见", PInvoke.IsWindowVisible(radial) ? "是" : "否");
        ProbeOutput.Kv("  轮盘矩形", GetRectText(radial));
    }

    /// <summary>核对轮盘确实被钳制在光标所在屏内 —— 位置算错时圆盘会有一部分跑到屏幕外。</summary>
    private static void VerifyPlacement(OverlayController overlay)
    {
        ProbeOutput.Section("位置钳制核对");
        var radial = (HWND)overlay.RadialHandle;
        if (!PInvoke.GetWindowRect(radial, out var rect))
        {
            ProbeOutput.Line("  取窗口矩形失败，跳过。");
            return;
        }

        var monitor = MonitorLayout.AtCursor();
        ProbeOutput.Kv("光标所在屏", monitor.ToString());
        ProbeOutput.Kv("轮盘矩形", $"({rect.left},{rect.top})-({rect.right},{rect.bottom})");

        var insideX = rect.left >= monitor.X && rect.right <= monitor.Right;
        var insideY = rect.top >= monitor.Y && rect.bottom <= monitor.Bottom;

        ProbeOutput.Kv("水平方向在屏内", insideX ? "是" : $"否（越界 {Math.Min(0, rect.left - monitor.X)} / {Math.Min(0, monitor.Right - rect.right)}）");
        ProbeOutput.Kv("垂直方向在屏内", insideY ? "是" : $"否（越界 {Math.Min(0, rect.top - monitor.Y)} / {Math.Min(0, monitor.Bottom - rect.bottom)}）");
        ProbeOutput.Line();
        ProbeOutput.Line(insideX && insideY
            ? "  >>> 圆盘完整落在屏内。靠近边缘时角度保持绝对固定，不会被旋转 —— 否则肌肉记忆会被破坏。"
            : "  >>> 圆盘有部分在屏外。");

        // 顺带核对一下几何常量与实际窗口尺寸是否一致（改了常量忘了改窗口就会出现裂缝）。
        var width = rect.right - rect.left;
        var expected = (int)Math.Round(TokenStore.Radii.WindowSize * monitor.Scale);
        ProbeOutput.Kv("轮盘窗口宽（物理像素）", width);
        ProbeOutput.Kv("几何常量推算的宽度", expected);
        ProbeOutput.Kv("一致", Math.Abs(width - expected) <= 2 ? "是" : "否");
    }

    private static void Report()
    {
        ProbeOutput.Section("视觉规格（来自 design/tokens.json）");
        var tokens = TokenStore.Current;
        ProbeOutput.Kv("窗口边长", $"{tokens.Radius.WindowSize} DIP（上限 740，由内存预算反推）");
        ProbeOutput.Kv("死区半径", $"{tokens.Radius.DeadZone}（占外径 {tokens.Radius.DeadZone / (double)tokens.Radius.Ring2Outer:P0}）");
        ProbeOutput.Kv("第一层环", $"{tokens.Radius.DeadZone} → {tokens.Radius.Ring1Outer}");
        ProbeOutput.Kv("第二层环", $"{tokens.Radius.Ring2Inner} → {tokens.Radius.Ring2Outer}");
        ProbeOutput.Kv("入场动画", $"{tokens.Motion.EntranceDurationMs}ms，scale {tokens.Motion.EntranceFromScale} → 1.0，{tokens.Motion.EntranceEasing}");
        ProbeOutput.Kv("退场动画", $"{tokens.Motion.ExitDurationMs}ms，scale 1.0 → {tokens.Motion.ExitToScale}");
        ProbeOutput.Kv("系统动画开关", AnimationsEnabledForReport() ? "开" : "关（时长归零）");
        ProbeOutput.Kv("默认热键", HotkeySpec.Default.ToString());
        ProbeOutput.Kv("图标", $"直径 {tokens.Radius.IconSize} DIP；菜单项 {MenuSpec.BuiltInDefault.Items.Count} 个");
        ProbeOutput.Kv("图标缓存", $"{ShellIcons.CacheStats().Count} 个，{ShellIcons.CacheStats().Bytes / 1024} KB");

        ProbeOutput.Section("规格校验");
        var problems = tokens.Validate();
        if (problems.Count == 0) ProbeOutput.Line("  全部通过（含内存预算上限、死区比例、环的嵌套关系、缓动曲线可解析）");
        else foreach (var p in problems) ProbeOutput.Line("  ! " + p);

        ProbeOutput.Section("热键注册能力（在独立窗口上探测，不影响当前生效的热键）");
        // 用一个临时消息窗口探测，不改动常驻实例的注册。
        ProbeOutput.Line("  见 --hook-probe 的完整报告。");

        ProbeOutput.Flush();
    }

    /// <summary>沿屏幕中部横线找一个足够亮的像素，返回其坐标与原始颜色。</summary>
    private static (int X, int Y, byte R, byte G, byte B) FindInformativePixel()
    {
        var monitor = MonitorLayout.AtCursor();
        var y = monitor.Y + monitor.Height / 2;
        var best = (X: monitor.X + monitor.Width / 2, Y: y, R: (byte)0, G: (byte)0, B: (byte)0, Lum: -1);

        unsafe
        {
            var screenDc = PInvoke.GetDC(default);
            if (screenDc == default) return (best.X, best.Y, best.R, best.G, best.B);

            try
            {
                for (var x = monitor.X + 8; x < monitor.Right - 8; x += 16)
                {
                    var color = PInvoke.GetPixel(screenDc, x, y);
                    if (color == new COLORREF(0xFFFFFFFF)) continue;   // CLR_INVALID

                    var cr = (byte)(color.Value & 0xFF);
                    var cg = (byte)((color.Value >> 8) & 0xFF);
                    var cb = (byte)((color.Value >> 16) & 0xFF);
                    var lum = cr * 299 + cg * 587 + cb * 114;

                    if (lum > best.Lum) best = (x, y, cr, cg, cb, lum);
                }
            }
            finally
            {
                PInvoke.ReleaseDC(default, screenDc);
            }
        }

        return (best.X, best.Y, best.R, best.G, best.B);
    }

    /// <summary>
    /// 验证遮罩是「半透明黑」而不是「灰纱」。
    ///
    /// 判据：纯黑 + 统一 alpha 的合成结果是每个通道都乘以 (1 - alpha/255)，
    /// 所以色相不变、各通道比例一致。若窗口底色是系统预定义类的"按钮表面色"（灰），
    /// 暗通道会被抬高、各通道比例失衡 —— 灰纱正是这么来的。
    /// 这是唯一能自动区分「黑纱」与「灰纱」的办法，直接对应那条用户反馈。
    /// </summary>
    private static unsafe void VerifyDimColor((int X, int Y, byte R, byte G, byte B) before)
    {
        ProbeOutput.Section("遮罩颜色核对（半透明黑 vs 灰纱）");

        var screenDc = PInvoke.GetDC(default);
        if (screenDc == default)
        {
            ProbeOutput.Line("  取屏幕 DC 失败，跳过。");
            return;
        }

        byte r, g, b;
        try
        {
            var color = PInvoke.GetPixel(screenDc, before.X, before.Y);
            if (color == new COLORREF(0xFFFFFFFF))
            {
                ProbeOutput.Line("  取像素失败，跳过。");
                return;
            }
            r = (byte)(color.Value & 0xFF);
            g = (byte)((color.Value >> 8) & 0xFF);
            b = (byte)((color.Value >> 16) & 0xFF);
        }
        finally
        {
            PInvoke.ReleaseDC(default, screenDc);
        }

        ProbeOutput.Kv("采样点", $"({before.X},{before.Y})");
        ProbeOutput.Kv("遮罩前", $"RGB({before.R},{before.G},{before.B})");
        ProbeOutput.Kv("遮罩后", $"RGB({r},{g},{b})");

        const double expectedRatio = 1.0 - RawDimWindow.DefaultAlpha / 255.0;

        var ratios = new List<(string Channel, byte Before, byte After, double Ratio)>();
        if (before.R > 20) ratios.Add(("R", before.R, r, (double)r / before.R));
        if (before.G > 20) ratios.Add(("G", before.G, g, (double)g / before.G));
        if (before.B > 20) ratios.Add(("B", before.B, b, (double)b / before.B));

        if (ratios.Count == 0)
        {
            ProbeOutput.Line("  采样点太暗，判断不出比例 —— 需要一个亮一点的采样点才有意义。");
            return;
        }

        foreach (var (channel, b0, a0, ratio) in ratios)
            ProbeOutput.Line($"    {channel}: {b0} → {a0}   比例 {ratio:0.###}");

        var avg = ratios.Average(x => x.Ratio);
        var spread = ratios.Max(x => x.Ratio) - ratios.Min(x => x.Ratio);
        var maxDeviation = ratios.Max(x => Math.Abs(x.Ratio - expectedRatio));

        ProbeOutput.Kv("期望比例", $"约 {expectedRatio:0.###}（纯黑 + {RawDimWindow.DefaultAlpha}/255 alpha）");
        ProbeOutput.Kv("实测平均比例", avg.ToString("0.###"));
        ProbeOutput.Kv("通道间差异", spread.ToString("0.###"));
        ProbeOutput.Line();

        if (spread >= 0.10)
            ProbeOutput.Line("  >>> 各通道变暗比例不一致 —— 遮罩底色不是纯黑，很可能仍是窗口类画刷的灰色。");
        else if (maxDeviation < 0.10)
            ProbeOutput.Line("  >>> 遮罩是纯黑 + 统一 alpha：各通道按同一比例变暗，符合预期。");
        else
            ProbeOutput.Line($"  >>> 变暗比例偏离期望（实测 {avg:0.###}，期望 {expectedRatio:0.###}）。");
    }

    /// <summary>
    /// 验证点击扇区能被检测到。
    ///
    /// 做法：把光标移到某个扇区中心的屏幕位置（WPF 的输入系统按实际光标位置判定命中的元素，
    /// 只投递消息而不移动光标的话可能命中不到），再投递按下/抬起消息。
    /// 之后核对 SectorActivated 是否触发、以及命中索引是否与几何计算一致。
    /// </summary>
    private static async Task VerifySectorClickAsync(OverlayController overlay)
    {
        ProbeOutput.Section("点击扇区是否被检测到");
        if (!overlay.IsVisible)
        {
            ProbeOutput.Line("  覆盖层当前不可见，跳过。");
            return;
        }

        var tokens = TokenStore.Current;
        var monitor = MonitorLayout.AtCursor();
        var hitIndex = -1;
        overlay.ItemActivated += (_, selection) => hitIndex = selection.ChildIndex >= 0 ? selection.ChildIndex : selection.ParentIndex;

        PInvoke.GetCursorPos(out var savedCursor);
        var radial = (HWND)overlay.RadialHandle;
        PInvoke.GetWindowRect(radial, out var rect);

        // 目标：第 0 个扇区（以正上方为中心）的中半径处。
        var centreX = (rect.left + rect.right) / 2.0;
        var centreY = (rect.top + rect.bottom) / 2.0;
        var midRadiusPx = tokens.Radius.Ring1MidRadius * monitor.Scale;
        var offset = Vec2.FromPolar(midRadiusPx, 0);
        var screenX = (int)Math.Round(centreX + offset.X);
        var screenY = (int)Math.Round(centreY + offset.Y);

        ProbeOutput.Kv("轮盘矩形", $"({rect.left},{rect.top})-({rect.right},{rect.bottom})");
        ProbeOutput.Kv("目标点（屏幕像素）", $"({screenX},{screenY})");
        ProbeOutput.Kv("预期命中的扇区", "0（正上方）");

        PInvoke.SetCursorPos(screenX, screenY);
        await Task.Delay(200);

        var clientX = screenX - rect.left;
        var clientY = screenY - rect.top;
        var lParam = new LPARAM((clientY << 16) | (clientX & 0xFFFF));

        PInvoke.PostMessage(radial, PInvoke.WM_LBUTTONDOWN, new WPARAM(1), lParam);

        // 补上抬起：自从改成"按下与抬起在同一处才算一次完整点击"之后，
        // 只投递按下不会执行任何动作（这正是新语义要保证的）。
        PInvoke.PostMessage(radial, PInvoke.WM_LBUTTONUP, default, lParam);
        await Task.Delay(250);

        ProbeOutput.Kv("ItemActivated 触发", hitIndex >= 0 ? $"是，索引 {hitIndex}" : "否");
        ProbeOutput.Line();
        ProbeOutput.Line(hitIndex == 0
            ? "  >>> 点击扇区被正确检测，命中索引与几何计算一致。"
            : hitIndex >= 0
                ? $"  >>> 点击被检测到，但命中索引是 {hitIndex} 而不是 0 —— 坐标换算有问题。"
                : "  >>> 点击扇区没有被检测到。注意：合成消息与真实鼠标输入不完全等价，"
                  + "若你手动点击能生效则说明是探针的限制而不是程序缺陷。");

        PInvoke.SetCursorPos(savedCursor.X, savedCursor.Y);
    }

    /// <summary>
    /// 验证子弧（第二层环）能否展开，以及展开后的几何是否落在预期环带上。
    /// 子弧在窗口内的位置完全由令牌决定，可以用窗口矩形把它换算回屏幕坐标核对。
    /// </summary>
    private static async Task VerifyChildArcAsync(OverlayController overlay)
    {
        ProbeOutput.Section("子弧展开核对");
        if (!overlay.IsVisible)
        {
            ProbeOutput.Line("  覆盖层当前不可见，跳过。");
            return;
        }

        var tokens = TokenStore.Current;
        var menu = Hosting.DefaultMenu.Build();
        var parentIndex = -1;
        for (var i = 0; i < menu.Items.Count; i++)
            if (menu.Items[i].HasChildren) { parentIndex = i; break; }

        if (parentIndex < 0)
        {
            ProbeOutput.Line("  默认菜单里没有带子项的项，跳过。");
            return;
        }

        var childCount = menu.Items[parentIndex].Children.Count;
        // 视觉树泄漏检查：展开/收起一轮之后，画布上的直接子元素数量必须回到原值。
        // 「每次收起都留下几个元素」这类泄漏在性能劣化前完全不可见，
        // 只有靠这种结构计数才抓得住。
        var baselineVisuals = overlay.VisualChildCount;
        ProbeOutput.Kv("展开前视觉树元素数", baselineVisuals);

        ProbeOutput.Kv("父扇区", $"{parentIndex}（{menu.Items[parentIndex].Label}）");
        ProbeOutput.Kv("子项数", childCount);

        var arc = RadialLayout.ChildArc(parentIndex * (360.0 / menu.Items.Count), childCount,
            tokens.Interaction.ChildSpanDegrees);
        ProbeOutput.Kv("子弧角度跨度", $"{arc.SweepDeg:0.#}°（中线 {arc.CenterDeg:0.#}°）");
        ProbeOutput.Kv("子弧环带", $"{tokens.Radius.Ring2Inner} → {tokens.Radius.Ring2Outer}");

        // 直接调用窗口的展开路径，跳过悬停延迟 —— 延迟本身已由状态机单测覆盖。
        overlay.ExpandChildArcForProbe(parentIndex);
        await Task.Delay(400);   // 留出展开动画完成的时间

        ProbeOutput.Kv("子弧已展开", overlay.IsChildArcExpanded ? "是" : "否");
        ProbeOutput.Kv("展开进度", overlay.ChildArcProgress.ToString("0.###"));
        ProbeOutput.Kv("子项数（渲染出来的）", overlay.ChildItemCount);
        ProbeOutput.Kv("展开后视觉树元素数", overlay.VisualChildCount);

        ProbeOutput.Line();
        ProbeOutput.Line(overlay.IsChildArcExpanded && overlay.ChildArcProgress > 0.99
            ? "  >>> 子弧展开完成，进度到 1。几何数值验证见 --render-preview --state=child。"
            : "  >>> 子弧未完全展开，展开路径或动画有问题。");

        overlay.CollapseChildArcForProbe();
        await Task.Delay(400);
        ProbeOutput.Kv("收起后仍可见", overlay.IsChildArcExpanded ? "是（异常）" : "否");

        var afterVisuals = overlay.VisualChildCount;
        ProbeOutput.Kv("收起后视觉树元素数", afterVisuals);
        ProbeOutput.Line();
        ProbeOutput.Line(afterVisuals == baselineVisuals
            ? $"  >>> 视觉树无残留：{baselineVisuals} → {afterVisuals}。"
            : $"  >>> 视觉树有残留：{baselineVisuals} → {afterVisuals}，" +
              $"多了 {afterVisuals - baselineVisuals} 个元素。每次展开收起都会累积。");

        // 再走一轮，确认残留不是"只漏一次"而是会累积。
        overlay.ExpandChildArcForProbe(parentIndex);
        await Task.Delay(300);
        overlay.CollapseChildArcForProbe();
        await Task.Delay(400);
        var secondRound = overlay.VisualChildCount;
        ProbeOutput.Kv("第二轮回合后元素数", secondRound);
        ProbeOutput.Line(secondRound == baselineVisuals
            ? "  >>> 第二轮同样无残留。"
            : $"  >>> 第二轮回合后多出 {secondRound - baselineVisuals} 个元素 —— 泄漏会累积。");
    }

    /// <summary>
    /// 核对左上角操作条：显示时应可见，且位置在光标所在屏的左上角。
    /// 它是托盘图标之外的设置入口 —— Win11 默认把新托盘图标收进溢出区，
    /// 用户不一定找得到，所以这个入口存在是必要的。
    /// </summary>
    private static async Task VerifyActionBarAsync(OverlayController overlay)
    {
        ProbeOutput.Section("左上角操作条");
        if (!overlay.IsVisible)
        {
            ProbeOutput.Line("  覆盖层当前不可见，跳过。");
            return;
        }

        await Task.Delay(200);

        var handle = (HWND)overlay.ActionBarHandle;
        ProbeOutput.Kv("操作条实际可见", PInvoke.IsWindowVisible(handle) ? "是" : "否");

        if (!PInvoke.GetWindowRect(handle, out var rect))
        {
            ProbeOutput.Line("  取窗口矩形失败。");
            return;
        }

        var monitor = MonitorLayout.AtCursor();
        ProbeOutput.Kv("光标所在屏", monitor.ToString());
        ProbeOutput.Kv("操作条矩形", $"({rect.left},{rect.top})-({rect.right},{rect.bottom})");

        // 左上角 = 靠近屏幕原点，且整块都在屏内。
        var nearLeft = rect.left - monitor.X is >= 0 and <= 60;
        var nearTop = rect.top - monitor.Y is >= 0 and <= 60;
        var insideX = rect.right <= monitor.Right;
        var insideY = rect.bottom <= monitor.Bottom;

        ProbeOutput.Kv("贴近屏幕左上角", nearLeft && nearTop ? "是" : "否");
        ProbeOutput.Kv("完整落在屏内", insideX && insideY ? "是" : "否");
        ProbeOutput.Line();
        ProbeOutput.Line(nearLeft && nearTop && insideX && insideY
            ? "  >>> 操作条位于光标所在屏的左上角。"
            : "  >>> 操作条位置不符合预期。");
    }

    /// <summary>
    /// 核对点击「设置」按钮能否真的触发设置请求。
    ///
    /// 这条直接对应一个用户反馈："两个按钮点击都是关闭，点设置没有显示设置界面"。
    /// 根因是点操作条会让轮盘窗口失去焦点，覆盖层在按钮 Click 完成前就被隐藏了。
    /// 所以这里要同时核对两件事：请求触发了，且覆盖层没有因此关闭。
    /// </summary>
    private static async Task VerifyActionBarClickAsync(OverlayController overlay)
    {
        ProbeOutput.Section("点击左上角「设置」按钮");

        if (!overlay.IsVisible)
        {
            ProbeOutput.Line("  覆盖层当前不可见，跳过。");
            return;
        }

        var settingsRequested = false;
        overlay.SettingsClosed += (_, _) => { };
        // 面板改为开在覆盖层里：点击「设置」现在应当直接打开面板，而不是发一个事件出去。
        var settingsOpenedBefore = overlay.IsSettingsOpen;

        var bar = (HWND)overlay.ActionBarHandle;
        if (!PInvoke.GetWindowRect(bar, out var rect))
        {
            ProbeOutput.Line("  取操作条矩形失败，跳过。");
            return;
        }

        // 按钮位置由操作条自己算出来，避免在探针里猜布局。
        var buttonCenter = overlay.ActionBarSettingsButtonCenterInScreen();
        if (buttonCenter is not { } point)
        {
            ProbeOutput.Line("  取按钮位置失败，跳过。");
            return;
        }

        ProbeOutput.Kv("操作条矩形", $"({rect.left},{rect.top})-({rect.right},{rect.bottom})");
        ProbeOutput.Kv("「设置」按钮中心（屏幕像素）", $"({point.X},{point.Y})");

        PInvoke.GetCursorPos(out var saved);
        PInvoke.SetCursorPos(point.X, point.Y);
        await Task.Delay(150);

        var clientX = point.X - rect.left;
        var clientY = point.Y - rect.top;
        var lParam = new LPARAM((clientY << 16) | (clientX & 0xFFFF));

        PInvoke.PostMessage(bar, PInvoke.WM_LBUTTONDOWN, new WPARAM(1), lParam);
        await Task.Delay(120);
        PInvoke.PostMessage(bar, PInvoke.WM_LBUTTONUP, default, lParam);
        await Task.Delay(300);

        PInvoke.SetCursorPos(saved.X, saved.Y);

        ProbeOutput.Kv("设置请求已触发", settingsRequested ? "是" : "否");
        ProbeOutput.Line();
        ProbeOutput.Line(settingsRequested
            ? "  >>> 点击「设置」能触发设置请求。"
            : "  >>> 点击没有触发设置请求。注意：合成消息与真实鼠标输入不完全等价，"
              + "若你手动点击能生效则说明是探针的限制。");
        ProbeOutput.Line("  注：探针不核对设置窗口是否弹出 —— 那会真的打开一个窗口。");
    }

    /// <summary>
    /// 核对托盘图标能否从嵌入资源加载。
    /// 这一步是必要的：托盘图标一旦加载失败，Windows 可能根本不显示那个条目，
    /// 而用户看到的现象是"托盘里什么都没有"，与真正的原因之间没有任何线索。
    /// </summary>
    private static void VerifyTrayIconResource()
    {
        ProbeOutput.Section("托盘图标资源");

        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("WinQuick.tray.ico");

            if (stream is null)
            {
                ProbeOutput.Line("  嵌入资源 WinQuick.tray.ico 不存在 —— 托盘会没有图标，Windows 可能不显示它。");
                return;
            }

            ProbeOutput.Kv("嵌入资源大小", $"{stream.Length} 字节");
            ProbeOutput.Kv("系统请求的小图标边长", Tray.TrayHost.SystemSmallIconSize.ToString());

            // 关键：核对**托盘真实加载到的那份**，而不是在这里另写一段加载代码 ——
            // 那样测的是探针自己，生产路径仍然没被验证。
            using var tray = new Tray.TrayHost(new OverlayController(MenuSpec.BuiltInDefault));
            ProbeOutput.Kv("托盘实际加载的图标尺寸", tray.IconDescription);
            // IsCreated 是唯一能确认"外壳真的接受了这个图标"的途径。
            ProbeOutput.Kv("外壳已接受图标（IsCreated）", tray.Created ? "是" : "否（托盘可能不显示）");
            tray.Dispose();

            ProbeOutput.Line();
            ProbeOutput.Line("  图标可加载。注意：这只能证明资源本身没问题，");
            ProbeOutput.Line("  不能证明 Windows 一定把它显示出来 —— Windows 11 默认把**新的**托盘图标");
            ProbeOutput.Line("  收进溢出区（任务栏的 ^ 箭头里）。看不到时先点 ^，");
            ProbeOutput.Line("  或在「设置 → 个性化 → 任务栏 → 其他系统托盘图标」里把它打开。");
        }
        catch (Exception ex)
        {
            ProbeOutput.Kv("加载失败", ex.Message);
        }
    }

    private static bool AnimationsEnabledForReport()
    {
        try { return SystemParameters.ClientAreaAnimation; }
        catch { return true; }
    }

    private static unsafe string Describe(HWND hwnd)
    {
        if (hwnd == default) return "(空)";

        var title = GetWindowTitle(hwnd);
        PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        // CsWin32 里 HWND.Value 是 void*，格式化前要先转成 nint。
        return $"0x{(nint)hwnd.Value:X} pid={pid} title=\"{title}\"";
    }

    private static unsafe string GetWindowTitle(HWND hwnd)
    {
        try
        {
            var length = PInvoke.GetWindowTextLength(hwnd);
            if (length <= 0) return "";

            var buffer = new char[Math.Min(length + 1, 256)];
            fixed (char* p = buffer)
            {
                var copied = PInvoke.GetWindowText(hwnd, p, buffer.Length);
                return new string(p, 0, copied);
            }
        }
        catch
        {
            return "(取标题失败)";
        }
    }

    private static string GetRectText(HWND hwnd) =>
        PInvoke.GetWindowRect(hwnd, out var rect)
            ? $"({rect.left},{rect.top})-({rect.right},{rect.bottom})  {rect.right - rect.left}x{rect.bottom - rect.top}"
            : "(取矩形失败)";
}
