using System.Windows;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;

namespace WinQuick.App.M0;

/// <summary>
/// 刷新闪烁探针 —— 回答"刷新内容时到底发生了哪些窗口级操作"。
///
/// 为什么要数而不是看：分层窗口（<c>AllowsTransparency</c> + 逐像素 alpha）的整块表面
/// 由进程通过 <c>UpdateLayeredWindow</c> 交给合成器。**改尺寸、换内容对象、发 SetWindowPos、
/// 显隐**都会让这块表面重建一次，观感上就是"闪一下"。而"闪"发生在两帧之间 ——
/// 进程内部看不见。能看见的只有"哪些操作发生了"，所以把它们数出来（见 <see cref="SurfaceOps"/>）。
///
/// 判据：**纯内容刷新一项都不该涨**（换文字、换配色、换菜单项、悬停、拖拽都算纯内容）。
/// 涨了就是机制问题，得改实现而不是改观感。
///
/// 每一步都同时打出面板高度：用户看到的"面板刷新时闪"，最可能的机制就是
/// "改内容顺手改了窗口尺寸"—— 而窗口是 SizeToContent="Height"，
/// 于是长文本加粗体这种事都会让整块表面重建。这件事只有高度数据能回答。
///
/// 用法：--flicker-probe
/// </summary>
public static class FlickerProbe
{
    /// <summary>每个动作之后的等待：够走完布局与一次合成，又不至于跨过置顶重申周期。</summary>
    private const int SettleMs = 450;

    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("flicker", "m4");
        ProbeOutput.Line("典型刷新动作 × 窗口级操作增量。纯内容刷新应当一项都不涨。");

        var results = new List<(string Name, bool Ok, string Detail)>();
        var notable = new List<string>();

        OverlayController? overlay = null;
        try
        {
            var menu = DefaultMenu.Build();
            overlay = new OverlayController(menu);
            overlay.Prewarm();

            overlay.Show();
            await Task.Delay(900);
            overlay.OpenSettings();
            await Task.Delay(900);

            var panel = overlay.SettingsPanelForProbe;
            var tokens = TokenStore.Current;

            ProbeOutput.Kv("面板高度 × 宽度", $"{panel?.PanelHeightForProbe ?? 0:0.#} × {panel?.PanelWidthForProbe ?? 0:0.#} DIP");
            ProbeOutput.Kv("轮盘状态（编辑态/项数/「＋」）", overlay.RadialStateForProbe.ToString());

            static Func<Task> Act(Action action) => () =>
            {
                action();
                return Task.CompletedTask;
            };

            async Task StepAsync(string name, Func<Task> action, bool contentOnly)
            {
                var before = SurfaceOps.Now();
                var zBefore = overlay!.ZOrder.ReassertCount;

                await action();
                await Task.Delay(SettleMs);

                var after = SurfaceOps.Now();
                var zDelta = overlay!.ZOrder.ReassertCount - zBefore;
                var delta = after.DeltaFrom(before);
                var zText = zDelta == 0 ? "重申 0" : $"重申 ×{zDelta}";

                ProbeOutput.Kv(name, delta);
                ProbeOutput.Kv("  （面板高度 / z 序）", $"{panel?.PanelHeightForProbe ?? 0:0.#} DIP / {zText}");

                if (delta != "无") notable.Add($"{name} → {delta}");

                if (contentOnly)
                    results.Add(($"纯内容刷新「{name}」不重建表面",
                        after.SurfaceRebuilds == before.SurfaceRebuilds && zDelta == 0,
                        $"增量：{delta}；{zText}"));
            }

            // ---- 静置 -------------------------------------------------------
            // 3 秒 = 至少三个置顶重申周期（800ms）。若计时器在无条件重申，
            // 这一步必然抓到 —— 静置的核对只等一个周期会得到间歇的结论，那比失败更坏。
            ProbeOutput.Section("静置：有没有自己在动的操作");
            {
                var before = SurfaceOps.Now();
                var zBefore = overlay.ZOrder.ReassertCount;
                await Task.Delay(3000);
                var after = SurfaceOps.Now();
                var zDelta = overlay.ZOrder.ReassertCount - zBefore;
                var delta = after.DeltaFrom(before);

                ProbeOutput.Kv("静置 3 秒", delta);
                ProbeOutput.Kv("  期间的 z 序重申", zDelta == 0 ? "0 次" : $"{zDelta} 次");
                if (delta != "无") notable.Add($"静置 3 秒 → {delta}");

                results.Add(("静置 3 秒无任何窗口级操作（含 z 序重申）",
                    delta == "无" && zDelta == 0,
                    $"增量：{delta}；重申 {zDelta} 次"));
            }

            // ---- 设置面板：只改内容 ----------------------------------------
            ProbeOutput.Section("设置面板：只改内容");
            await StepAsync("页脚文本→长",
                Act(() => panel?.SetFooterForProbe("已加入「Adobe Photoshop 2025（64 位）」，位于一级环第 3 位。")), true);
            await StepAsync("页脚文本→短",
                Act(() => panel?.SetFooterForProbe("已保存。")), true);
            await StepAsync("热键状态→长",
                Act(() => panel?.SetHotkeyStatusForProbe("该组合不可用（错误 1409：热键已被其它程序占用），换一个试试。")), true);
            await StepAsync("遮罩滑块→0.55",
                Act(() => panel?.SetDimOpacityForTest(0.55)), true);
            await StepAsync("悬停延迟→220/380",
                Act(() => panel?.SetDelaysForText("220", "380")), true);
            await StepAsync("强调色→#FF6EA8FE",
                Act(() => panel?.SetAccentForTest("#FF6EA8FE")), true);

            // ---- 轮盘：换菜单 / 编辑态 / 悬停 / 拖拽 -------------------------
            ProbeOutput.Section("轮盘：换菜单、编辑态、悬停、拖拽");

            await StepAsync("换菜单（同 8 项）", Act(() => overlay.RebuildForMenu(menu)), true);

            var nine = menu with { Items = menu.Items.Append(new MenuItemSpec { Label = "第 9 项" }).ToList() };
            await StepAsync("换菜单（8→9 项）", Act(() => overlay.RebuildForMenu(nine)), true);
            await StepAsync("编辑态开", Act(() => overlay.SetEditModeForProbe(true)), true);

            // 拖拽：编辑态下按下就直接进入拖拽（不必再等长按）。
            // 槽位按**当前**布局算 —— 编辑态下多了「＋」那格，9 项 + 「＋」= 10 格。
            var slots = overlay.RadialStateForProbe.ItemCount + 1;
            var midRadius = (tokens.Radius.DeadZone + tokens.Radius.Ring1Outer) / 2.0;

            Point SlotCenter(int index)
            {
                var offset = Vec2.FromPolar(midRadius, RadialLayout.Distribute(slots)[index].CenterDeg);
                return new Point(
                    tokens.Radius.WindowSize / 2.0 + offset.X,
                    tokens.Radius.WindowSize / 2.0 + offset.Y);
            }

            var dragFrom = SlotCenter(0);
            var dragTo = SlotCenter(3);
            await StepAsync("拖拽重排（第 0 格 → 第 3 格）", Act(() =>
            {
                overlay.PressForProbe(dragFrom.X, dragFrom.Y);
                overlay.MovePointerForProbe(dragTo.X, dragTo.Y);
                overlay.ReleaseForProbe(dragTo.X, dragTo.Y);
            }), true);

            ProbeOutput.Kv("  拖拽后是否仍处于拖拽态", overlay.IsDraggingForProbe ? "是（异常）" : "否（正确）");

            await StepAsync("编辑态关", Act(() => overlay.SetEditModeForProbe(false)), true);
            await StepAsync("回到 8 项菜单", Act(() => overlay.RebuildForMenu(menu)), true);
            await StepAsync("悬停第 2 个扇区", Act(() => overlay.HoverForProbe(2)), true);

            // Items 是 IReadOnlyList，没有 FindIndex —— 手写一遍，不为了这个引 LINQ。
            var parentIndex = -1;
            for (var i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i].Children is { Count: > 0 }) { parentIndex = i; break; }
            }
            if (parentIndex >= 0)
            {
                await StepAsync($"展开子弧（第 {parentIndex} 项）",
                    Act(() => overlay.ExpandChildArcForProbe(parentIndex)), true);
                await StepAsync("收起子弧", Act(() => overlay.CollapseChildArcForProbe()), true);
            }
            else
            {
                ProbeOutput.Kv("展开子弧", "跳过：这份菜单里没有带子项的项");
            }

            // ---- 面板与覆盖层的收放 ----------------------------------------
            // 这两条本来就要重建表面（显隐必然重建），所以不核"零操作"，
            // 只核一条真正该守住的：**收放不该改轮盘的尺寸，也不该换掉它的内容对象**。
            ProbeOutput.Section("面板与覆盖层的收放");

            {
                var before = SurfaceOps.Now();
                overlay.CloseSettings();
                await Task.Delay(SettleMs);
                overlay.OpenSettings();
                await Task.Delay(900);
                var after = SurfaceOps.Now();

                ProbeOutput.Kv("关面板→再开", after.DeltaFrom(before));
                results.Add(("收放设置面板不改轮盘尺寸、不换轮盘内容对象",
                    after.WheelResize == before.WheelResize && after.ContentSwap == before.ContentSwap,
                    after.DeltaFrom(before)));
            }

            {
                var before = SurfaceOps.Now();
                overlay.Hide();
                await Task.Delay(SettleMs);
                overlay.Show();
                await Task.Delay(900);
                var after = SurfaceOps.Now();

                ProbeOutput.Kv("收起覆盖层→再显示", after.DeltaFrom(before));
                if (after.DeltaFrom(before) != "无") notable.Add($"收起再显示 → {after.DeltaFrom(before)}");
                results.Add(("收放覆盖层不改轮盘尺寸、不换轮盘内容对象",
                    after.WheelResize == before.WheelResize && after.ContentSwap == before.ContentSwap,
                    after.DeltaFrom(before)));
            }

            overlay.Hide();
        }
        catch (Exception ex)
        {
            results.Add(("探针完整跑完", false, ex.Message));
            ProbeOutput.Line();
            ProbeOutput.Line("探针未捕获异常: " + ex);
        }
        finally
        {
            overlay?.Dispose();
        }

        // ---- 汇总 ----------------------------------------------------------
        // 只列"涨了"的动作。全都在这里出现就说明机制还没修；
        // 一条都不出现（只有显隐那两条）才说明纯内容刷新是干净的。
        ProbeOutput.Section("有窗口级操作的动作");
        if (notable.Count == 0)
            ProbeOutput.Line("  一条都没有 —— 纯内容刷新没有触发任何窗口级操作。");
        foreach (var line in notable) ProbeOutput.Line("  " + line);

        ProbeOutput.Section("结果");
        var failed = 0;
        foreach (var (name, ok, detail) in results)
        {
            ProbeOutput.Kv((ok ? "通过  " : "不通过") + " " + name, detail);
            if (!ok) failed++;
        }

        ProbeOutput.Line();
        ProbeOutput.Line(failed == 0
            ? $"  >>> {results.Count} 项全部通过。"
            : $"  >>> {failed} / {results.Count} 项不通过，见上。");

        ProbeOutput.Flush();
    }
}
