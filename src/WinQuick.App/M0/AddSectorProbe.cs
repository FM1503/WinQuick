using System.Windows;
using WinQuick.App.Diagnostics;
using WinQuick.App.Design;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace WinQuick.App.M0;

/// <summary>
/// 「＋」扇区探针 —— 核对"在环里直接加一项"这条路径。
///
/// 为什么必须单独核：这条路径跨了三层（视图命中 → 窗口分派 → 常驻层写盘重建），
/// 而中间任何一层断掉的表现都是"点了没反应" —— 一个既不报错也不崩溃的静默失败。
/// 光看代码看不出断在哪。
///
/// 做法上有一点是刻意的：**点击走的是窗口真正的分派函数**（<c>ClickForProbe</c>
/// 调的就是鼠标事件调的那个 <c>HandleClick</c>），而不是探针自己重新判断一遍。
/// 否则探针验证的是"探针的规则"，不是程序的规则 —— 之前已经在"遮罩点击关闭"
/// 上踩过一次这个坑（文档写着已实现，实际从未生效）。
///
/// 用法：--add-sector-probe
/// </summary>
public static class AddSectorProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("add-sector", "m4");
        ProbeOutput.Line("核对环里的「＋」扇区：命中、点击分派、上限、加项后位置与落点。");

        var results = new List<(string Name, bool Ok, string Detail)>();

        OverlayController? overlay = null;
        try
        {
            var menu = DefaultMenu.Build();
            overlay = new OverlayController(menu);
            overlay.Prewarm();

            // 事件录音：点击之后核对"该发的发了、不该发的没发"。
            int? addRequested = null;
            (int Parent, int Child)? activated = null;
            overlay.AddItemRequested += (_, parentIndex) => addRequested = parentIndex;
            overlay.ItemActivated += (_, selection) => activated = selection;

            void ResetRecorders()
            {
                addRequested = null;
                activated = null;
            }

            overlay.Show();
            await Task.Delay(600);

            ProbeOutput.Section("第一层环");
            var tokens = TokenStore.Current;
            var rootAdd = overlay.AddSectorIndexForProbe;

            ProbeOutput.Kv("菜单项数", menu.Items.Count);
            ProbeOutput.Kv("「＋」扇区索引", rootAdd);
            results.Add(("第一层末尾有「＋」扇区",
                rootAdd == menu.Items.Count,
                $"索引 {rootAdd}，期望 {menu.Items.Count}"));

            // 每个扇区（含「＋」）取各自中心角、环带中径处的点，核对命中的是自己。
            // 采样点用 Vec2.FromPolar 生成，与渲染用的是同一套极坐标 ——
            // 这个探针要查的是**路由**，几何本身由 --render-preview 逐像素核对。
            var midRadius = (tokens.Radius.DeadZone + tokens.Radius.Ring1Outer) / 2.0;
            var slotCount = rootAdd >= 0 ? menu.Items.Count + 1 : menu.Items.Count;
            var spans = RadialLayout.Distribute(slotCount);

            var mismatches = new List<string>();
            for (var i = 0; i < slotCount; i++)
            {
                var local = ToLocal(Vec2.FromPolar(midRadius, spans[i].CenterDeg), tokens.Radius.WindowSize);
                var hit = overlay.HitTestForProbe(local.X, local.Y);
                if (hit.Zone != HitZone.Sector || hit.Index != i)
                    mismatches.Add($"扇区{i}（{spans[i].CenterDeg:0.#}°）→ {hit.Zone}/{hit.Index}");
            }

            ProbeOutput.Kv("扇区中心命中核对", mismatches.Count == 0
                ? $"{slotCount} 个扇区全部命中自己"
                : string.Join("；", mismatches));
            results.Add(("每个扇区的中心都命中自己", mismatches.Count == 0,
                mismatches.Count == 0 ? $"{slotCount} 个" : string.Join("；", mismatches)));

            ProbeOutput.Line();

            // ---- 点击「＋」 --------------------------------------------------
            var addPoint = ToLocal(Vec2.FromPolar(midRadius, spans[rootAdd].CenterDeg),
                tokens.Radius.WindowSize);
            ResetRecorders();
            overlay.ClickForProbe(addPoint.X, addPoint.Y);

            ProbeOutput.Kv("点第一层「＋」→ AddItemRequested", addRequested?.ToString() ?? "(没有)");
            ProbeOutput.Kv("　同时触发了 ItemActivated", activated is null ? "否" : $"是 {activated}");
            results.Add(("点第一层「＋」请求加项",
                addRequested == -1 && activated is null,
                $"parent={addRequested?.ToString() ?? "无"}，activated={activated?.ToString() ?? "无"}"));

            // ---- 点击真实叶子项 ----------------------------------------------
            // 找一个没有子项的项：它应当是"执行"，而不是"请求加项"。
            var leaf = -1;
            for (var i = 0; i < menu.Items.Count; i++)
                if (!menu.Items[i].HasChildren) { leaf = i; break; }

            if (leaf >= 0)
            {
                var leafPoint = ToLocal(Vec2.FromPolar(midRadius, spans[leaf].CenterDeg),
                    tokens.Radius.WindowSize);
                ResetRecorders();
                overlay.ClickForProbe(leafPoint.X, leafPoint.Y);

                ProbeOutput.Kv($"点叶子项 #{leaf}（{menu.Items[leaf].Label}）→ ItemActivated",
                    activated?.ToString() ?? "(没有)");
                ProbeOutput.Kv("　同时请求了加项", addRequested is null ? "否" : $"是 {addRequested}");
                results.Add(("点叶子项是执行而不是加项",
                    activated is { Child: -1 } && activated.Value.Parent == leaf && addRequested is null,
                    $"activated={activated?.ToString() ?? "无"}，add={addRequested?.ToString() ?? "无"}"));
            }

            // ---- 子弧里的「＋」 ----------------------------------------------
            ProbeOutput.Section("第二层子弧");
            var parentIndex = -1;
            for (var i = 0; i < menu.Items.Count; i++)
                if (menu.Items[i].HasChildren) { parentIndex = i; break; }

            if (parentIndex < 0)
            {
                ProbeOutput.Line("  默认菜单里没有带子项的项，跳过。");
            }
            else
            {
                var childCount = menu.Items[parentIndex].Children.Count;
                overlay.ExpandChildArcForProbe(parentIndex);
                await Task.Delay(400);

                var childAdd = overlay.ChildAddSectorIndexForProbe;
                ProbeOutput.Kv("父项", $"{parentIndex}（{menu.Items[parentIndex].Label}）");
                ProbeOutput.Kv("子项数", childCount);
                ProbeOutput.Kv("子弧「＋」索引", childAdd);
                results.Add(("子弧末尾有「＋」扇区",
                    childAdd == childCount,
                    $"索引 {childAdd}，期望 {childCount}"));

                var band = tokens.Radius.Ring2;
                var childMidRadius = band.At(0.5);
                var childSlots = childAdd >= 0 ? childCount + 1 : childCount;
                var childArc = RadialLayout.ChildArc(
                    spans[parentIndex].CenterDeg, childSlots, tokens.Interaction.ChildSpanDegrees);
                var childSpans = RadialLayout.DistributeWithin(childArc, childSlots);

                // 子环里也逐个核对：真实子项命中自己，「＋」命中「＋」。
                var childMismatches = new List<string>();
                for (var i = 0; i < childSlots; i++)
                {
                    var local = ToLocal(Vec2.FromPolar(childMidRadius, childSpans[i].CenterDeg),
                        tokens.Radius.WindowSize);
                    var hit = overlay.HitTestForProbe(local.X, local.Y);
                    if (hit.Zone != HitZone.ChildSector || hit.Index != i)
                        childMismatches.Add($"子扇区{i}（{childSpans[i].CenterDeg:0.#}°）→ {hit.Zone}/{hit.Index}");
                }

                ProbeOutput.Kv("子扇区中心命中核对", childMismatches.Count == 0
                    ? $"{childSlots} 个全部命中自己"
                    : string.Join("；", childMismatches));
                results.Add(("子弧每个扇区的中心都命中自己", childMismatches.Count == 0,
                    childMismatches.Count == 0 ? $"{childSlots} 个" : string.Join("；", childMismatches)));

                if (childAdd >= 0)
                {
                    var childAddPoint = ToLocal(
                        Vec2.FromPolar(childMidRadius, childSpans[childAdd].CenterDeg),
                        tokens.Radius.WindowSize);
                    ResetRecorders();
                    overlay.ClickForProbe(childAddPoint.X, childAddPoint.Y);

                    ProbeOutput.Kv("点子弧「＋」→ AddItemRequested", addRequested?.ToString() ?? "(没有)");
                    ProbeOutput.Kv("　同时触发了 ItemActivated", activated is null ? "否" : $"是 {activated}");
                    results.Add(("点子弧「＋」请求加进该父项",
                        addRequested == parentIndex && activated is null,
                        $"parent={addRequested?.ToString() ?? "无"}，期望 {parentIndex}"));
                }

                // 子弧里的真实子项仍然是"执行"。
                var childPoint = ToLocal(Vec2.FromPolar(childMidRadius, childSpans[0].CenterDeg),
                    tokens.Radius.WindowSize);
                ResetRecorders();
                overlay.ClickForProbe(childPoint.X, childPoint.Y);

                ProbeOutput.Kv("点子项 #0 → ItemActivated", activated?.ToString() ?? "(没有)");
                results.Add(("子项仍然是执行而不是加项",
                    activated is { Child: 0 } && activated.Value.Parent == parentIndex && addRequested is null,
                    $"activated={activated?.ToString() ?? "无"}，add={addRequested?.ToString() ?? "无"}"));

                overlay.CollapseChildArcForProbe();
                await Task.Delay(400);
            }

            // ---- 加一项之后：位置不跳、落点仍然正确 --------------------------
            ProbeOutput.Section("加一项之后（模拟写盘后重建）");
            var beforeBounds = overlay.RadialPhysicalBounds;
            ProbeOutput.Kv("重建前轮盘矩形", beforeBounds?.ToString() ?? "(不可见)");

            var grown = menu with
            {
                Items = menu.Items.Append(new MenuItemSpec
                {
                    Label = "探针新增项",
                    Action = MenuAction.Launch("cmd.exe"),
                }).ToList(),
            };

            overlay.RebuildForMenu(grown);
            await Task.Delay(700);

            var afterBounds = overlay.RadialPhysicalBounds;
            ProbeOutput.Kv("重建后轮盘矩形", afterBounds?.ToString() ?? "(不可见)");
            ProbeOutput.Kv("新「＋」索引", overlay.AddSectorIndexForProbe.ToString());

            results.Add(("加项后轮盘位置不变",
                beforeBounds is { } b && afterBounds is { } a
                && b.X == a.X && b.Y == a.Y && b.Width == a.Width && b.Height == a.Height,
                $"{beforeBounds?.ToString() ?? "无"} → {afterBounds?.ToString() ?? "无"}"));

            results.Add(("加项后「＋」仍排在所有项之后",
                overlay.AddSectorIndexForProbe == grown.Items.Count,
                $"索引 {overlay.AddSectorIndexForProbe}，期望 {grown.Items.Count}"));

            // 落点核对：轮盘中心处的点击必须由轮盘窗口拿走。
            // 「看得见却点不动」正是 z 序错了的表现，而重建窗口是重新插入 topmost band，
            // 最可能在这一点上出问题。
            if (afterBounds is { } rect)
            {
                var centerX = rect.X + rect.Width / 2;
                var centerY = rect.Y + rect.Height / 2;
                var hitWindow = PInvoke.WindowFromPoint(new System.Drawing.Point(centerX, centerY));
                var radialHandle = overlay.RadialHandle;

                ProbeOutput.Kv("轮盘中心处的落点窗口",
                    hitWindow == radialHandle ? "轮盘窗口本身" : $"0x{(nint)hitWindow:X}（不是轮盘）");
                results.Add(("加项后轮盘中心仍然点得到",
                    hitWindow == (HWND)radialHandle,
                    $"WindowFromPoint → 0x{(nint)hitWindow:X}，轮盘 → 0x{radialHandle:X}"));
            }

            // ---- 满 12 项时不再显示「＋」 ------------------------------------
            ProbeOutput.Section("满 12 项时");
            var full = menu with
            {
                Items = Enumerable.Range(1, 12)
                    .Select(i => new MenuItemSpec
                    {
                        Label = $"占位项 {i}",
                        Action = MenuAction.Launch("cmd.exe"),
                    })
                    .ToList(),
            };

            overlay.RebuildForMenu(full);
            await Task.Delay(700);

            ProbeOutput.Kv("12 项时的「＋」索引", overlay.AddSectorIndexForProbe.ToString());
            ProbeOutput.Kv("12 项时的渲染扇区数（应仍能命中第 12 项）",
                DescribeSectorHit(overlay, full, tokens));

            results.Add(("菜单满 12 项时不再显示「＋」",
                overlay.AddSectorIndexForProbe == -1,
                $"索引 {overlay.AddSectorIndexForProbe}"));

            overlay.Hide();
        }
        catch (Exception ex)
        {
            // 探针自己崩了也要把"没核对完"这件事记进结果 ——
            // 否则只报告已累积的那部分，读起来就像是"全部通过"。
            results.Add(("探针完整跑完", false, ex.Message));
            ProbeOutput.Line();
            ProbeOutput.Line("探针未捕获异常: " + ex);
        }
        finally
        {
            overlay?.Dispose();
        }

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

    /// <summary>把相对圆心的极坐标偏移换算成窗口内坐标。</summary>
    private static Point ToLocal(Vec2 offset, int windowSize) =>
        new(windowSize / 2.0 + offset.X, windowSize / 2.0 + offset.Y);

    /// <summary>12 项菜单下，第 12 项（逆时针贴着正上方那一格）是否能被命中。</summary>
    private static string DescribeSectorHit(OverlayController overlay, MenuSpec menu, DesignTokens tokens)
    {
        var count = menu.Items.Count;
        var spans = RadialLayout.Distribute(count);
        var midRadius = (tokens.Radius.DeadZone + tokens.Radius.Ring1Outer) / 2.0;
        var local = ToLocal(Vec2.FromPolar(midRadius, spans[count - 1].CenterDeg), tokens.Radius.WindowSize);
        var hit = overlay.HitTestForProbe(local.X, local.Y);
        return $"第 {count} 项中心 → {hit.Zone}/{hit.Index}";
    }
}
