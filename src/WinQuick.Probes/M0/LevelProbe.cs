using System.Windows;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using WinQuick.Core.Design;
using WinQuick.Core.Geometry;
using WinQuick.Core.Interaction;
using Windows.Win32;

namespace WinQuick.App.M0;

/// <summary>
/// 层级导航探针 —— 核对「同时只看到两级」这套交互（用户从 preview/wheel2-preview.html 选定的方案）。
///
/// 为什么必须单独核：这套交互有四个地方"错了也不报错"——
/// 下钻之后环 1 画的还是不是那一层、深层项点下去能不能执行、死区两半各去哪、
/// 悬停是不是真的不再展开。它们全部表现为"点了没反应 / 去了别的层"，
/// 既不会抛异常也不会留下日志，靠看代码判断不了。
///
/// 做法上沿用既有约定：**点击走窗口真正的分派函数**（<c>ClickForProbe</c> 调的就是
/// 鼠标事件调的那个 <c>HandleClick</c>），几何用 Core 的同一套数学独立算一遍。
/// 每条核对都配一个对照，能真的失败。
///
/// 用法：--level-probe
/// </summary>
public static class LevelProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("level", "m4");
        ProbeOutput.Line("核对两级滑动窗口：环1=父层、环2=当前层、下钻后旧环2 搬到环1。");

        var results = new List<(string Name, bool Ok, string Detail)>();

        OverlayController? overlay = null;
        try
        {
            // 用一棵**合成**的三级菜单：默认菜单只有两级，而这条路径要验的正是"第三层"。
            // 合成也让探针可复现 —— 不受用户当前 menus.json 的影响。
            var menu = BuildThreeLevelMenu();
            overlay = new OverlayController(menu);
            overlay.Prewarm();
            overlay.Show();
            await Task.Delay(600);

            var tokens = TokenStore.Current;

            // 找一条真有第三层的链：一级里带子项的项 → 它的子项里再带子项的项。
            var rootIndex = -1;
            var deepChildIndex = -1;
            for (var i = 0; i < menu.Items.Count; i++)
            {
                var child = menu.Items[i].Children
                    .Select((c, index) => (c, index))
                    .FirstOrDefault(t => t.c.HasChildren);
                if (child.c is null) continue;

                rootIndex = i;
                deepChildIndex = child.index;
                break;
            }

            if (rootIndex < 0)
            {
                ProbeOutput.Line("  这份菜单没有第三层（没有'自带子项的子项'），无法核对下钻。");
                ProbeOutput.Flush();
                return;
            }

            ProbeOutput.Section("起点：一级");
            ProbeOutput.Kv("用的一级项", $"[{rootIndex}] {menu.Items[rootIndex].Label}");
            ProbeOutput.Kv("它的第几项还有子项", $"[{deepChildIndex}] {menu.Items[rootIndex].Children[deepChildIndex].Label}");
            ProbeOutput.Kv("起始深度", overlay.LevelDepthForProbe.ToString());
            results.Add(("起点在第一层",
                overlay.LevelDepthForProbe == 0 && !overlay.IsChildArcExpanded,
                $"深度 {overlay.LevelDepthForProbe}，子弧可见 {overlay.IsChildArcExpanded}"));

            // 右半「上一层」在第一层必须是灰的；换肤的就地重刷不许把灰冲掉
            //（第一版正是那里统一刷成亮色 —— 用户报"没置灰"的根因）。
            var greyAtRoot = overlay.CenterBackAlphaForProbe;
            ProbeOutput.Kv("第一层时右半透明度", greyAtRoot.ToString("0.00"));
            results.Add(("第一层时右半「上一层」是灰的（明显暗于左半）",
                greyAtRoot is > 0 and <= 0.35,
                $"alpha {greyAtRoot:0.00}（灰 ≈ 0.22，亮 ≈ 0.55）"));

            overlay.RebuildForAppearance(RingForm.Pie);
            await Task.Delay(400);
            var greyAfterSkin = overlay.CenterBackAlphaForProbe;
            ProbeOutput.Kv("换肤就地重刷后右半透明度", greyAfterSkin.ToString("0.00"));
            results.Add(("换肤就地重刷不丢灰态（回归锁）",
                greyAfterSkin is > 0 and <= 0.35,
                $"alpha {greyAfterSkin:0.00}"));

            // ---- 环 1 的分组项：点击 = 展开（深度不变） ----------------------
            ProbeOutput.Section("展开：点环 1 的分组项");
            var ring1Point = Ring1Point(menu, rootIndex, tokens);
            var ring1Hit = overlay.ClickForProbe(ring1Point.X, ring1Point.Y);
            await Task.Delay(500);

            var ring2Before = overlay.Ring2LabelsForProbe;
            ProbeOutput.Kv("命中", $"{ring1Hit.Zone}/{ring1Hit.Index}");
            ProbeOutput.Kv("深度", overlay.LevelDepthForProbe.ToString());
            ProbeOutput.Kv("环 2 各项", string.Join(" / ", ring2Before));
            var brightAtDepth1 = overlay.CenterBackAlphaForProbe;
            results.Add(("深于一层时右半「上一层」变亮",
                brightAtDepth1 >= 0.4,
                $"alpha {brightAtDepth1:0.00}"));

            results.Add(("点环 1 的分组项后：深度 1、子弧真的展开、环 2 是它的子项",
                overlay.LevelDepthForProbe == 1
                && overlay.ChildArcParentForProbe == rootIndex
                && overlay.IsChildArcExpanded
                && ring2Before.Count == menu.Items[rootIndex].Children.Count,
                $"深度 {overlay.LevelDepthForProbe}，父项下标 {overlay.ChildArcParentForProbe}，"
                + $"子弧可见 {overlay.IsChildArcExpanded}，环 2 共 {ring2Before.Count} 项"));

            var ring1Before = overlay.Ring1LabelsForProbe;

            // ---- 悬停不再展开（对照：同一个位置点一下就会展开） --------------
            // 这一条要能失败才有意义：探针把真指针放到分组扇区上，等过旧的展开延迟（200ms）
            // 再看深度有没有自己变。对照组紧接着真的点一下 —— 免得"没展开"是因为指针压根不在扇区上。
            ProbeOutput.Section("悬停：不再展开（全部改成点击）");
            var hoverBefore = overlay.LevelDepthForProbe;
            PInvoke.GetCursorPos(out var savedCursor);
            PInvoke.SetCursorPos((int)Math.Round(ring1Point.X), (int)Math.Round(ring1Point.Y));
            await Task.Delay(600);
            var hoverAfter = overlay.LevelDepthForProbe;
            PInvoke.SetCursorPos(savedCursor.X, savedCursor.Y);   // 把指针还回去
            ProbeOutput.Kv("指针停在另一个分组扇区上 600ms 后的深度", hoverAfter.ToString());
            results.Add(("悬停不再自动展开或收起（深度不变）",
                hoverAfter == hoverBefore,
                $"{hoverBefore} → {hoverAfter}（旧实现里 200ms 就会展开）"));

            // ---- 环 2 的分组项：点击 = 下钻（旧环 2 整层搬到环 1） -----------
            ProbeOutput.Section("下钻：点环 2 的分组项");
            var ring2Point = Ring2Point(
                menu.Items.Count, rootIndex, menu.Items[rootIndex].Children.Count, deepChildIndex, tokens);
            var ring2Hit = overlay.ClickForProbe(ring2Point.X, ring2Point.Y);

            // 位移动画应当在点下去之后立刻在跑（这是"旧环 2 搬到环 1"那一段）
            var shiftingRightAfter = overlay.IsLevelShiftingForProbe;
            await Task.Delay(700);
            var shiftingAfterWait = overlay.IsLevelShiftingForProbe;

            var ring1After = overlay.Ring1LabelsForProbe;
            ProbeOutput.Kv("命中", $"{ring2Hit.Zone}/{ring2Hit.Index}");
            ProbeOutput.Kv("深度", overlay.LevelDepthForProbe.ToString());
            ProbeOutput.Kv("下钻后环 1", string.Join(" / ", ring1After));
            ProbeOutput.Kv("（下钻前环 2）", string.Join(" / ", ring2Before));
            results.Add(("下钻后环 1 逐项等于旧环 2（整层搬过来、顺序一致）",
                ring2Before.Count > 0 && ring1After.SequenceEqual(ring2Before),
                $"旧环 2 {ring2Before.Count} 项 → 新环 1 {ring1After.Count} 项"));
            results.Add(("下钻后深度 2、环 1 不再是原来的那一层",
                overlay.LevelDepthForProbe == 2
                && !overlay.Ring1LabelsForProbe.SequenceEqual(ring1Before),
                $"深度 {overlay.LevelDepthForProbe}"));
            results.Add(("位移动画点下去就开始、等一会儿就结束（回调不残留）",
                shiftingRightAfter && !shiftingAfterWait,
                $"点击瞬间 {shiftingRightAfter}，700ms 后 {shiftingAfterWait}，" +
                $"活跃回调 {overlay.LevelShiftHookCountForProbe}"));

            // ---- 第三层的叶子：点下去能执行（旧实现在这里报「还没有配置动作」）----
            ProbeOutput.Section("第三层：叶子项可执行");
            IReadOnlyList<int>? activated = null;
            overlay.ItemActivated += (_, steps) => activated = steps;

            var leafIndex = -1;
            var ring2Now = overlay.Ring2LabelsForProbe;
            for (var i = 0; i < ring2Now.Count; i++)
                if (!overlay.ChildIndexHasChildrenForProbe(i)) { leafIndex = i; break; }

            if (leafIndex < 0)
            {
                results.Add(("第三层能点到叶子项", false, "环 2 里没有叶子项"));
            }
            else
            {
                // 此时环 1 = 第二层那一圈，父项是 deepChildIndex
                var leafPoint = Ring2Point(
                    menu.Items[rootIndex].Children.Count, deepChildIndex, ring2Now.Count, leafIndex, tokens);
                var leafHit = overlay.ClickForProbe(leafPoint.X, leafPoint.Y);
                await Task.Delay(400);

                var expected = MenuLevels.Resolve(
                    menu, [rootIndex, deepChildIndex, leafIndex]);

                ProbeOutput.Kv("命中", $"{leafHit.Zone}/{leafHit.Index}");
                ProbeOutput.Kv("报上来的路径", activated is null ? "(没有)" : string.Join(",", activated));
                ProbeOutput.Kv("路径解析出的项", expected?.Label ?? "(解析不出来)");
                results.Add(("第三层的叶子项：报出完整三段路径且能解析到那一项",
                    activated is { Count: 3 }
                    && activated[0] == rootIndex && activated[1] == deepChildIndex && activated[2] == leafIndex
                    && expected is not null,
                    $"路径 [{(activated is null ? "无" : string.Join(",", activated))}] → 「{expected?.Label}」"));
            }

            // ---- 死区两半：左 = Esc（关闭），右 = 返回上一层 -----------------
            // 采样必须打**内部点**（左上 315° / 右上 45°）：只点 3 点钟和 9 点钟的话，
            // 「上半/下半」与「左半/右半」两种划分在那两个点结论一致 ——
            // 第一版就是这么全绿通过的，而实现其实划成了上/下两半。
            // 右半先验（2→1→0，第一层时是灰的无操作），左半 = 关闭放到最后（关了就测不了别的）。
            ProbeOutput.Section("中心：左半 Esc（关闭）、右半 返回上一层");
            var depthBeforeCenter = overlay.LevelDepthForProbe;
            ProbeOutput.Kv("此时深度", depthBeforeCenter.ToString());

            var upRight = DeadZonePoint(45, tokens);      // 1:30 —— 右半内部点
            var upLeft = DeadZonePoint(315, tokens);      // 10:30 —— 左半内部点

            var backHit = overlay.ClickForProbe(upRight.X, upRight.Y);
            await Task.Delay(400);
            var depthAfterBack = overlay.LevelDepthForProbe;
            ProbeOutput.Kv("点右半（1:30 内部点）", $"{backHit.Zone}/{backHit.Index} → 深度 {depthAfterBack}");
            results.Add(("点中心右半（内部点）= 返回上一层",
                depthBeforeCenter >= 2 && backHit.Zone == HitZone.DeadZone
                && depthAfterBack == depthBeforeCenter - 1,
                $"{depthBeforeCenter} → {depthAfterBack}"));

            var backHit2 = overlay.ClickForProbe(upRight.X, upRight.Y);
            await Task.Delay(400);
            results.Add(("再点右半 = 又退一层（1 → 0）",
                overlay.LevelDepthForProbe == 0,
                $"深度 {overlay.LevelDepthForProbe}"));

            // 第一层时右半是灰的：点了不动（对照：左半在同状态点下去会直接关，见最后一节）
            overlay.ClickForProbe(upRight.X, upRight.Y);
            await Task.Delay(250);
            results.Add(("第一层时点右半是无操作",
                overlay.LevelDepthForProbe == 0 && overlay.IsVisible,
                $"深度 {overlay.LevelDepthForProbe}，可见 {overlay.IsVisible}"));

            // ---- 编辑态：下钻不生效（编辑只覆盖两级以内） --------------------
            ProbeOutput.Section("编辑态：不允许再往下钻");
            overlay.SetEditModeForProbe(true);
            await Task.Delay(400);
            overlay.ExpandChildArcForProbe(rootIndex);
            await Task.Delay(400);

            var editDepthBefore = overlay.LevelDepthForProbe;
            var editPoint = Ring2Point(
                menu.Items.Count, rootIndex, menu.Items[rootIndex].Children.Count, deepChildIndex, tokens);
            overlay.ClickForProbe(editPoint.X, editPoint.Y);
            await Task.Delay(400);

            ProbeOutput.Kv("编辑态下点环 2 的分组项", $"深度 {editDepthBefore} → {overlay.LevelDepthForProbe}");
            results.Add(("编辑态下点环 2 的分组项不会下钻",
                overlay.LevelDepthForProbe == editDepthBefore,
                $"深度 {overlay.LevelDepthForProbe}"));

            overlay.SetEditModeForProbe(false);
            await Task.Delay(300);

            // ---- 左半 = Esc：在任何状态下点它都直接关闭整棵轮盘 ------------
            // 放在最后：关了就没法继续测。用的是**左半内部点**（10:30）。
            ProbeOutput.Section("中心左半 = Esc（关闭）");
            var escHit = overlay.ClickForProbe(upLeft.X, upLeft.Y);
            await Task.Delay(400);
            ProbeOutput.Kv("点左半（10:30 内部点）",
                $"{escHit.Zone}/{escHit.Index} → 覆盖层可见 {overlay.IsVisible}");
            results.Add(("点中心左半（内部点）= 关闭整棵轮盘",
                escHit.Zone == HitZone.DeadZone && !overlay.IsVisible,
                $"可见 {overlay.IsVisible}（关闭 = Esc 半在任何深度都直达，与 Esc 键的逐层退出不同）"));

            // ---- 收尾：残留检查 ----------------------------------------------
            overlay.CollapseChildArcForProbe();
            await Task.Delay(400);
            ProbeOutput.Section("收尾");
            ProbeOutput.Kv("回第一层后的深度", overlay.LevelDepthForProbe.ToString());
            ProbeOutput.Kv("层级位移活跃回调", overlay.LevelShiftHookCountForProbe.ToString());
            results.Add(("回第一层后没有残留的位移回调",
                overlay.LevelDepthForProbe == 0 && overlay.LevelShiftHookCountForProbe == 0,
                $"深度 {overlay.LevelDepthForProbe}，回调 {overlay.LevelShiftHookCountForProbe}"));
        }
        catch (Exception ex)
        {
            results.Add(("探针整体跑完没有异常", false, ex.GetType().Name + " " + ex.Message));
        }
        finally
        {
            overlay?.Hide();
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

    /// <summary>
    /// 三级菜单：一级 8 项（8 是既有的角度约定基准），第 8 项带 4 个子项，
    /// 其中第一个子项自己再带 3 个孙项 —— 这样才有"环 2 的分组项点下去要进第三层"这件事。
    /// </summary>
    private static MenuSpec BuildThreeLevelMenu()
    {
        var leaves = new List<MenuItemSpec>
        {
            new() { Label = "叶子甲" },
            new() { Label = "叶子乙" },
            new() { Label = "叶子丙" },
        };

        var group = new List<MenuItemSpec>
        {
            new() { Label = "子分组", Children = leaves },
            new() { Label = "子项二" },
            new() { Label = "子项三" },
            new() { Label = "子项四" },
        };

        var items = new List<MenuItemSpec>
        {
            new() { Label = "一级一" },
            new() { Label = "一级二" },
            new() { Label = "一级三" },
            new() { Label = "一级四" },
            new() { Label = "一级五" },
            new() { Label = "一级六" },
            new() { Label = "一级七" },
            new() { Label = "一级八（分组）", Children = group },
        };

        return new MenuSpec("probe-level", "层级探针菜单", items);
    }

    /// <summary>环 1 里第 index 项中心的窗口内坐标。</summary>
    private static Point Ring1Point(MenuSpec menu, int index, DesignTokens tokens)
    {
        var spans = RadialLayout.Distribute(menu.Items.Count);
        var midRadius = (tokens.Radius.DeadZone + tokens.Radius.Ring1Outer) / 2.0;
        return ToLocal(Vec2.FromPolar(midRadius, spans[index].CenterDeg), tokens.Radius.WindowSize);
    }

    /// <summary>
    /// 环 2（子弧）里第 index 项中心的窗口内坐标。
    /// 几何用 Core 的同一套数学独立算：子弧以**父项在环 1 里的中心角**为中线、跨度由 ChildArc 决定。
    ///
    /// 参数要显式给"环 1 有几项、父项在环 1 里排第几"：深度 2 时环 1 已经是
    /// 第二层那一圈（不再是菜单一级），沿用一级的角度会算到别处去。
    /// </summary>
    private static Point Ring2Point(
        int ring1ItemCount, int parentIndexInRing1, int childCount, int childIndex, DesignTokens tokens)
    {
        var ring1Spans = RadialLayout.Distribute(ring1ItemCount);
        var parentCenter = ring1Spans[parentIndexInRing1].CenterDeg;

        var arc = RadialLayout.ChildArc(parentCenter, childCount, tokens.Interaction.ChildSpanDegrees);
        var spans = RadialLayout.DistributeWithin(arc, childCount);
        var band = tokens.Radius.Ring2;
        var midRadius = band.Inner + band.Width / 2.0;
        return ToLocal(Vec2.FromPolar(midRadius, spans[childIndex].CenterDeg), tokens.Radius.WindowSize);
    }

    /// <summary>
    /// 死区里某个角度方向、半径 0.6×死区 处的窗口内坐标 —— 刻意不打在正左/正右的边界上，
    /// 要的是某一半的**内部点**（理由见「中心」一节开头的注释）。
    /// </summary>
    private static Point DeadZonePoint(double angleDeg, DesignTokens tokens)
        => ToLocal(Vec2.FromPolar(tokens.Radius.DeadZone * 0.6, angleDeg), tokens.Radius.WindowSize);

    private static Point ToLocal(Vec2 offset, int windowSize)
        => new(windowSize / 2.0 + offset.X, windowSize / 2.0 + offset.Y);
}
