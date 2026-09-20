using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using H.NotifyIcon;
using WinQuick.App.Diagnostics;
using WinQuick.App.Design;
using WinQuick.Core.Design;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using WinQuick.App.Interop;

namespace WinQuick.App.M0;

/// <summary>
/// M0.3 空闲内存实测。
///
/// 这是调研里唯一一个我没能找到可信数字的指标 —— 网上报的 200-400MB 全是完整 GUI 应用，
/// 不是「常驻托盘 + 预热窗口」这种形态。而 60MB 是本项目的硬指标之一，
/// 所以必须实测，且要分阶段量，才能知道钱花在哪。
///
/// 关键：必须让窗口「真正 Show 过一次」再测。（预热窗口第一次渲染会分配字体、
/// 渲染目标、D3D 表面，那些都在首次显示时才发生。）只测「创建了但没显示」会低估。
/// </summary>
public static class MemoryProbe
{
    private static readonly List<(string Stage, long WorkingSet, long Private)> Stages = [];

    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("mem");
        ProbeOutput.Line("测的是「常驻托盘 + 预热窗口」形态的空闲内存，分阶段量以定位开销来源。");
        ProbeOutput.Line("Release 构建才有意义；Debug 会带 JIT 调试开销。");

        ProbeOutput.Section("运行时");
        ProbeOutput.Line(NativeDisplay.DescribeEnvironment());
        ProbeOutput.Kv("构建配置", IsDebug() ? "Debug（数值偏大，仅供对比）" : "Release");
        ProbeOutput.Kv("渲染层级", NativeDisplay.RenderTierDescription());

        var warmupMs = cli.GetInt("warmup", 12) * 1000;
        var cycles = cli.GetInt("cycles", 0);
        var visibleMs = cli.GetInt("visible", 3) * 1000;

        TaskbarIcon? tray = null;
        var rawDims = new List<RawDimWindow>();
        TransparentShellWindow? radial = null;

        try
        {
            Sample("0. 应用刚启动（无窗口、无托盘）");

            tray = CreateTray();
            await SettleAsync(1200);
            Sample("1. + 托盘图标");

            // 遮罩层用原生 Win32 窗口。此前用 WPF 窗口做过对照，实测多占 61MB
            // 且销毁后不归还（因为 WPF 为纯色窗口也分配了完整渲染目标），
            // 原始数据留在 artifacts/m0/mem-wpf-dim-vs-raw.txt。
            rawDims = CreateRawDimWindows();
            await SettleAsync(1200);
            Sample($"2. + 原生 Win32 遮罩窗口 x{rawDims.Count}（隐藏，LWA_ALPHA）");

            radial = CreateRadialWindow(cli);
            await SettleAsync(1200);
            Sample("3. + 轮盘窗口（隐藏，AllowsTransparency）");

            // 首次显示才会分配字体/渲染目标/D3D 表面，这一步不能省。
            ShowAll(rawDims, radial);
            await SettleAsync(1500);
            HideAll(rawDims, radial);
            await SettleAsync(1500);
            Sample("4. 首次唤出后（这才是可外推的稳态基线）");

            for (var i = 0; i < cycles; i++)
            {
                ShowAll(rawDims, radial);
                await SettleAsync(visibleMs);
                HideAll(rawDims, radial);
                await SettleAsync(1000);
            }

            if (cycles > 0)
            {
                await SettleAsync(3000);
                Sample($"5. {cycles} 次唤出/关闭循环后");
            }

            // ── 视觉树重建的增量（模拟"换皮肤/改菜单"这类真实操作）──
            //
            // **必须用真实窗口量。** 我第一版是在一个裸 Canvas 上重建 RadialRingView ——
            // 那从不把树渲染进分层窗口，所以完全测不到窗口侧的开销，得出的"会回落、
            // 不是泄漏"结论对产品路径不成立（用户实测常驻会停在 ~170MB 专用工作集）。
            // 这一版建/显示一个**真的 RadialWindow**，然后走产品里"改菜单"的那条路径
            // （ApplyMenu）重建 N 次，并在前后各强制回收一次。
            var rebuilds = cli.GetInt("rebuilds", 0);
            if (rebuilds > 0)
            {
                var menu = Hosting.DefaultMenu.Build();

                // --no-icons：把图标全部去掉（走占位图路径）。
                // 用来做**归因**对照：如果去掉图标后重建不再累积，就说明累积挂在
                // 「Image 元素 + 位图渲染进分层窗口」这条路径上。
                if (cli.Has("no-icons"))
                {
                    menu = menu with { Items = menu.Items.Select(StripIcons).ToList() };
                    ProbeOutput.Line("已启用 --no-icons：图标替换为占位图");
                }

                var wheel = new RadialWindow(menu);
                // 放屏幕外：探针不该在用户屏幕上闪东西。
                wheel.WindowStartupLocation = WindowStartupLocation.Manual;
                wheel.Left = -32000;
                wheel.Top = -32000;
                wheel.ShowActivated = false;
                wheel.Show();
                await SettleAsync(1500);
                Sample("5. 真实轮盘窗口（分层，已显示一次）");

                // --churn=skin（默认）：模拟用户点皮肤 —— 走 RebuildForSkin（现在是**就地换材质**）。
                // --churn=menu：模拟改菜单 —— 走 ApplyMenu（结构变了，必须重建）。
                var churn = cli.GetString("churn", "skin");
                for (var i = 0; i < rebuilds; i++)
                {
                    if (churn == "menu") wheel.ApplyMenu(menu);
                    else wheel.RebuildForSkin(RingForm.Pie);
                    await SettleAsync(40);
                }

                await SettleAsync(1500);
                Sample($"6. {rebuilds} 次「{churn}」后（真实窗口）");

                // **能失败的功能核对**：换皮肤之后，这一格的材质必须真的换成新规格的颜色。
                // 就地换材质那条路一旦写漏（比如忘了重上悬停态/忘了改描边），
                // 表现就是"点了皮肤没反应"，而那是肉眼才看得出来的 —— 这里先钉住它。
                {
                    var before = wheel.SectorStyleForProbe(1);
                    var problem = TokenStore.ApplySkin("contrast");
                    wheel.RebuildForSkin(RingForm.Pie);
                    var after = wheel.SectorStyleForProbe(1);
                    var expectFill = TokenBrushes.Brush(
                        TokenStore.Current.Color.SectorFill,
                        TokenStore.Current.Color.SectorFillAlpha).ToString();

                    ProbeOutput.Section("换皮肤（就地路径）的功能核对");
                    ProbeOutput.Kv("皮肤应用", problem ?? "成功");
                    ProbeOutput.Kv("换前填色", before.Fill);
                    ProbeOutput.Kv("换后填色", after.Fill);
                    ProbeOutput.Kv("规格里的填色", expectFill);
                    ProbeOutput.Kv("描边宽度", $"{before.StrokeWidth} → {after.StrokeWidth}");
                    ProbeOutput.Kv("结论",
                        after.Fill != before.Fill && after.Fill == expectFill
                            ? "通过（材质真的换了）"
                            : "**失败**（就地更新没把新规格套上去）");

                    TokenStore.ApplySkin(SkinCatalog.DefaultId);
                    wheel.RebuildForSkin(RingForm.Pie);
                    await SettleAsync(300);
                }

                ForceCollect();
                await SettleAsync(2000);
                Sample("7. 强制回收后");

                await SettleAsync(5000);
                Sample("8. 再静置 5 秒");

                wheel.Close();
            }

            await SettleAsync(Math.Max(0, warmupMs - 8000));
            Sample("8. 长稳定后");

            Report();
            await HoldAsync(cli);
        }
        catch (Exception ex)
        {
            ProbeOutput.Line();
            ProbeOutput.Line("探针异常: " + ex);
            Report();
        }
        finally
        {
            try { tray?.Dispose(); } catch { /* 托盘清理失败不该影响测量结果输出 */ }
            foreach (var d in rawDims) { try { d.Dispose(); } catch { } }
            Application.Current.Shutdown(0);
        }
    }

    /// <summary>把一棵菜单项子树里的图标全部去掉（--no-icons 归因对照用）。</summary>
    private static MenuItemSpec StripIcons(MenuItemSpec item) =>
        item with { IconPath = null, Children = item.Children.Select(StripIcons).ToList() };

    /// <summary>
    /// 强制回收：连做三轮（含终结器与压缩），让 WPF 的非托管渲染数据也有机会被放掉。
    /// 只用来**分辨**"真泄漏"与"还没回收"，不是产品行为。
    /// </summary>
    private static void ForceCollect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        try
        {
            Application.Current?.Dispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch
        {
            // 探针里拿不到 Dispatcher 也无所谓：上面三轮回收已经是主要手段。
        }
    }

    /// <summary>
    /// --hold=N：测完保持进程存活 N 秒（窗口留在稳态、隐藏），
    /// 便于在任务管理器里肉眼核对，或用 Get-Counter 从外部采样「专用工作集」——
    /// 那才是任务管理器默认显示的列，和 Process.WorkingSet64 不是一回事。
    /// </summary>
    private static async Task HoldAsync(CliOptions cli)
    {
        var hold = cli.GetInt("hold", 0);
        if (hold <= 0) return;

        ProbeOutput.Section("保持存活供外部采样");
        ProbeOutput.Kv("PID", Environment.ProcessId);
        ProbeOutput.Kv("保持时长", hold + " 秒");
        ProbeOutput.Line();
        ProbeOutput.Line("  Get-Counter \"\\Process(WinQuick)\\Working Set - Private\"");
        ProbeOutput.Flush();
        await SettleAsync(hold * 1000);
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    private static TaskbarIcon CreateTray()
    {
        var tray = new TaskbarIcon
        {
            ToolTipText = "WinQuick M0 内存探针",
            Visibility = Visibility.Visible,
        };
        try
        {
            tray.Icon = System.Drawing.SystemIcons.Application;
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("托盘图标加载失败（不影响内存测量）: " + ex.Message);
        }
        return tray;
    }

    private static List<RawDimWindow> CreateRawDimWindows()
    {
        // 遮罩用统一 alpha（40% 黑），不需要逐像素合成，所以原生窗口足够且几乎免费。
        var snap = NativeDisplay.Capture();
        var count = Math.Max(1, snap.MonitorCount);
        var list = new List<RawDimWindow>(count);

        for (var i = 0; i < count; i++)
        {
            var w = new RawDimWindow(alpha: 102);
            w.SetPhysicalBounds(snap.VirtualX, snap.VirtualY, snap.VirtualWidth, snap.VirtualHeight);
            list.Add(w);
        }
        return list;
    }

    private static TransparentShellWindow CreateRadialWindow(CliOptions cli)
    {
        var variant = cli.GetString("radial", "transparent");
        var size = cli.GetInt("size", 1000);
        var w = new TransparentShellWindow(size, opaque: variant == "opaque");
        w.Show();
        w.Visibility = Visibility.Hidden;
        return w;
    }

    private static void ShowAll(List<RawDimWindow> dims, TransparentShellWindow radial)
    {
        foreach (var d in dims) { d.Show(); d.ReassertTopmost(); }
        radial.Visibility = Visibility.Visible;
    }

    private static void HideAll(List<RawDimWindow> dims, TransparentShellWindow radial)
    {
        radial.Visibility = Visibility.Hidden;
        foreach (var d in dims) d.Hide();
    }

    private static async Task SettleAsync(int ms)
    {
        if (ms <= 0) return;
        await Task.Delay(ms);
    }

    private static void Sample(string stage)
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        Stages.Add((stage, p.WorkingSet64, p.PrivateMemorySize64));
    }

    private static void Report()
    {
        ProbeOutput.Section("内存实测（分阶段）");
        ProbeOutput.Line($"  {"阶段",-58} {"工作集(MB)",-12} {"私有字节(MB)",-12} {"增量(MB)"}");
        ProbeOutput.Line("  " + new string('-', 100));

        long prevWs = 0;
        foreach (var (stage, ws, priv) in Stages)
        {
            var delta = prevWs == 0 ? 0 : (ws - prevWs) / 1024.0 / 1024.0;
            var deltaText = prevWs == 0 ? "" : (delta >= 0 ? "+" : "") + delta.ToString("0.0");
            ProbeOutput.Line($"  {stage,-58} {ws / 1024.0 / 1024.0,-12:0.0} {priv / 1024.0 / 1024.0,-12:0.0} {deltaText}");
            prevWs = ws;
        }

        ProbeOutput.Section("其他指标");
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        ProbeOutput.Kv("线程数", p.Threads.Count);
        ProbeOutput.Kv("句柄数", p.HandleCount);
        ProbeOutput.Kv("GC 堆（不强制回收）", $"{GC.GetTotalMemory(false) / 1024.0 / 1024.0:0.0} MB");
        ProbeOutput.Kv("GC 堆（强制回收后）", $"{GC.GetTotalMemory(true) / 1024.0 / 1024.0:0.0} MB");
        ProbeOutput.Kv("GC 代数", string.Join("/", Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount)));
        ProbeOutput.Kv("峰值工作集", $"{p.PeakWorkingSet64 / 1024.0 / 1024.0:0.0} MB");

        var final = Stages.Count > 0 ? Stages[^1].WorkingSet : 0;
        ProbeOutput.Section("对照 60MB 目标");
        ProbeOutput.Kv("最终空闲工作集", $"{final / 1024.0 / 1024.0:0.0} MB");
        ProbeOutput.Kv("目标", "60 MB");
        ProbeOutput.Line();
        ProbeOutput.Line(final / 1024.0 / 1024.0 <= 60
            ? "  >>> 达标。纯 WPF 路线的内存预算成立。"
            : "  >>> 未达标。需要优化（ReadyToRun / 关闭工作集 GC / 不预热第二窗口 / 精简托盘）,");
        if (final / 1024.0 / 1024.0 > 60)
            ProbeOutput.Line("      我在方案里承诺过不达标会如实汇报，这里是原始数据。");

        ProbeOutput.Flush();
    }
}

/// <summary>
/// 轮盘层占位窗口：AllowsTransparency=true + WindowStyle=None，尺寸只覆盖圆盘包围盒。
/// M0 阶段放最少的图形，让渲染目标/字体真正分配出来；M2 会换成真正的扇区渲染。
/// </summary>
internal sealed class TransparentShellWindow : Window
{
    /// <param name="opaque">
    /// true = 对照组：普通不透明窗口，用来分辨「WPF 渲染栈的一次性开销」与
    /// 「逐像素透明通路特有的开销」。两者数值接近就说明这笔开销无法靠改窗口属性省掉。
    /// </param>
    public TransparentShellWindow(double size, bool opaque = false)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = !opaque;       // 逐像素 alpha，代价是 GPU→CPU 回读
        Background = opaque ? Brushes.Black : Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Width = size;
        Height = size;
        Left = -32000;                      // 预热但移出可视区
        Top = -32000;

        // 触发一回布局与文字渲染，把字体缓存和渲染目标都预热出来。
        Content = new Grid
        {
            Children =
            {
                new Ellipse
                {
                    Width = size * 0.5,
                    Height = size * 0.5,
                    Fill = new SolidColorBrush(Color.FromArgb(220, 32, 32, 32)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                    StrokeThickness = 1,
                },
                new TextBlock
                {
                    Text = "WinQuick",
                    FontSize = 14,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }
}
