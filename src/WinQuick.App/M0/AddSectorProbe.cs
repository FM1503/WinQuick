using System.Windows;
using WinQuick.App.Diagnostics;
using WinQuick.App.Design;
using WinQuick.App.Hosting;
using WinQuick.App.Radial;
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

            var tokens = TokenStore.Current;

            // ---- 日常态：不该有「＋」 ----------------------------------------
            // 编辑入口属于设置场景。日常使用时轮盘里只该有用户自己的项，
            // 否则每次唤出都多一个一直在那儿的"编辑"按钮。
            ProbeOutput.Section("日常态（未进设置）");
            ProbeOutput.Kv("「＋」扇区索引", overlay.AddSectorIndexForProbe.ToString());
            results.Add(("日常态下不显示「＋」扇区",
                overlay.AddSectorIndexForProbe == -1,
                $"索引 {overlay.AddSectorIndexForProbe}"));

            var normalMid = (tokens.Radius.DeadZone + tokens.Radius.Ring1Outer) / 2.0;
            var normalSpans = RadialLayout.Distribute(menu.Items.Count);
            var lastItemPoint = ToLocal(
                Vec2.FromPolar(normalMid, normalSpans[^1].CenterDeg), tokens.Radius.WindowSize);
            var normalHit = overlay.HitTestForProbe(lastItemPoint.X, lastItemPoint.Y);
            ProbeOutput.Kv("最后一项中心（应命中该项而不是「＋」）",
                $"{normalHit.Zone}/{normalHit.Index}");
            results.Add(("日常态下最后一项占满整个扇区",
                normalHit.Zone == HitZone.Sector && normalHit.Index == menu.Items.Count - 1,
                $"{normalHit.Zone}/{normalHit.Index}"));

            // ---- 编辑态由"设置面板是否打开"驱动 ------------------------------
            // 这一段走的是**正式路径**（overlay.OpenSettings），不是探针后门：
            // 用户要求的正是"点左上角设置或从托盘进设置后，「＋」才在轮盘上出现"。
            ProbeOutput.Section("打开设置后（正式路径）");
            overlay.OpenSettings();
            await Task.Delay(700);

            ProbeOutput.Kv("设置面板已打开", overlay.IsSettingsOpen ? "是" : "否");
            ProbeOutput.Kv("「＋」扇区索引", overlay.AddSectorIndexForProbe.ToString());
            results.Add(("打开设置后轮盘上出现「＋」",
                overlay.IsSettingsOpen && overlay.AddSectorIndexForProbe == menu.Items.Count,
                $"面板={overlay.IsSettingsOpen}，索引={overlay.AddSectorIndexForProbe}"));

            // 关掉面板**不再**收回「＋」：编辑态现在是独立状态、有自己的出口
            //（Esc / 点中心 / 点环外 / 关覆盖层）。用户可能想关掉面板继续在轮盘上拖拽排版，
            // 替他收掉反而打断操作。这一条锁的就是这个新契约。
            overlay.CloseSettings();
            await Task.Delay(500);
            ProbeOutput.Kv("关闭设置后的「＋」索引", overlay.AddSectorIndexForProbe.ToString());
            results.Add(("关闭设置面板后编辑态仍在（编辑态已独立于面板）",
                !overlay.IsSettingsOpen && overlay.AddSectorIndexForProbe >= 0,
                $"面板={overlay.IsSettingsOpen}，索引={overlay.AddSectorIndexForProbe}"));

            // 后面的命中与分派核对需要编辑态，用探针后门直接按上 ——
            // 正式路径已经被上面两段验过了。
            ProbeOutput.Section("编辑态");
            overlay.SetEditModeForProbe(true);
            await Task.Delay(300);

            var rootAdd = overlay.AddSectorIndexForProbe;
            ProbeOutput.Kv("菜单项数", menu.Items.Count);
            ProbeOutput.Kv("「＋」扇区索引", rootAdd);
            results.Add(("编辑态下第一层末尾有「＋」扇区",
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

            // ---- 点击「＋」：先给选择 -----------------------------------------
            var addPoint = ToLocal(Vec2.FromPolar(midRadius, spans[rootAdd].CenterDeg),
                tokens.Radius.WindowSize);
            ResetRecorders();
            overlay.ClickForProbe(addPoint.X, addPoint.Y);

            ProbeOutput.Kv("点第一层「＋」弹出的面板", overlay.PanelKindForProbe);
            ProbeOutput.Kv("　面板给出的选项", string.Join(" / ", overlay.PanelRowLabelsForProbe));
            ProbeOutput.Kv("　是否已经请求加项（应当还没）", addRequested?.ToString() ?? "没有（对）");

            // 用户要的是"能加带子级选项的项"，所以这个面板必须同时给出两条路 ——
            // 只给"添加程序"的话，建分组这条路径就还是不存在。
            var labels = overlay.PanelRowLabelsForProbe;
            var hasProgram = labels.Any(l => l.Contains("添加程序"));
            var hasGroup = labels.Any(l => l.Contains("新建分组"));
            results.Add(("第一层「＋」先出面板、不直接进文件浏览器",
                overlay.PanelKindForProbe == "AddChoice" && addRequested is null,
                $"kind={overlay.PanelKindForProbe}，add={addRequested?.ToString() ?? "无"}"));

            results.Add(("「＋」面板同时给出加程序与建分组",
                hasProgram && hasGroup, string.Join(" / ", labels)));

            // 选「添加程序…」→ 这时才发加项请求。
            var programRow = labels.ToList().FindIndex(l => l.Contains("添加程序"));
            ResetRecorders();
            overlay.InvokePanelRowForProbe(programRow);

            ProbeOutput.Kv("选「添加程序…」→ AddItemRequested", addRequested?.ToString() ?? "(没有)");
            ProbeOutput.Kv("　同时触发了 ItemActivated", activated is null ? "否" : $"是 {activated}");
            results.Add(("选「添加程序…」后才请求加项",
                addRequested == -1 && activated is null && !overlay.PanelAttached,
                $"parent={addRequested?.ToString() ?? "无"}"));

            // ---- 完整点击才执行 -----------------------------------------------
            // 用户报的：鼠标按住就直接执行打开了。按错想挪开再松手已经来不及，
            // 程序已经启动完 —— 所以改成"按下与抬起在同一处"才算一次完整点击。
            ProbeOutput.Section("完整点击才执行");

            // 这一节要求"点击会执行"，所以必须先确保不在编辑态 ——
            // 编辑态下点击扇区不执行（编辑时误触启动程序的代价太大）。
            // 以前这些前置状态是靠上一节留下的，现在编辑态独立了，就得显式摆好。
            overlay.SetEditModeForProbe(false);
            await Task.Delay(250);

            var leafIndex = -1;
            for (var i = 0; i < menu.Items.Count; i++)
                if (!menu.Items[i].HasChildren) { leafIndex = i; break; }

            var otherIndex = -1;
            for (var i = 0; i < menu.Items.Count; i++)
                if (i != leafIndex && !menu.Items[i].HasChildren) { otherIndex = i; break; }

            if (leafIndex >= 0 && otherIndex >= 0)
            {
                var leafPoint = ToLocal(Vec2.FromPolar(midRadius, spans[leafIndex].CenterDeg),
                    tokens.Radius.WindowSize);
                var otherPoint = ToLocal(Vec2.FromPolar(midRadius, spans[otherIndex].CenterDeg),
                    tokens.Radius.WindowSize);

                // 1) 只按下 —— 不该执行
                ResetRecorders();
                overlay.PressForProbe(leafPoint.X, leafPoint.Y);
                ProbeOutput.Kv("只按下时是否执行", activated is null ? "没有（正确）" : $"执行了 {activated}");
                results.Add(("只按下不执行（可以按住后反悔）",
                    activated is null, activated?.ToString() ?? "无"));

                // 2) 同一扇区内松开 —— 执行
                overlay.ReleaseForProbe(leafPoint.X, leafPoint.Y);
                ProbeOutput.Kv("在同一扇区松开后", activated is null ? "没执行（异常）" : $"执行了 {activated}");
                results.Add(("同一扇区内按下又松开才算一次完整点击",
                    activated is { Child: -1 } && activated.Value.Parent == leafIndex,
                    activated?.ToString() ?? "无"));

                // 3) 按下 A、挪到 B 再松开 —— 不该执行（尤其不该执行 B）
                ResetRecorders();
                overlay.PressForProbe(leafPoint.X, leafPoint.Y);
                overlay.ReleaseForProbe(otherPoint.X, otherPoint.Y);
                ProbeOutput.Kv("按下后挪到另一个扇区再松开", activated is null
                    ? "没有执行（正确）" : $"执行了 {activated}（不该）");
                results.Add(("按下后挪到别的扇区松开不会执行（不会误开另一个程序）",
                    activated is null, activated?.ToString() ?? "无"));

                // 4) 按下后挪到环外再松开 —— 不该执行
                var outsidePoint = ToLocal(Vec2.FromPolar(midRadius * 2.2, spans[leafIndex].CenterDeg),
                    tokens.Radius.WindowSize);
                ResetRecorders();
                overlay.PressForProbe(leafPoint.X, leafPoint.Y);
                overlay.ReleaseForProbe(outsidePoint.X, outsidePoint.Y);
                ProbeOutput.Kv("按下后挪到环外再松开", activated is null
                    ? "没有执行（正确）" : $"执行了 {activated}（不该）");
                results.Add(("按下后挪到环外松开不会执行", activated is null,
                    activated?.ToString() ?? "无"));
            }

            // 「＋」同样要完整点击才弹出面板。
            // 「＋」只在编辑态存在，所以这一段要先把它打开 ——
            // 上面那段为了"点击会执行"把编辑态关掉了。
            overlay.SetEditModeForProbe(true);
            await Task.Delay(300);

            if (overlay.AddSectorIndexForProbe >= 0)
            {
                rootAdd = overlay.AddSectorIndexForProbe;
                var plusPoint = ToLocal(Vec2.FromPolar(midRadius, spans[rootAdd].CenterDeg),
                    tokens.Radius.WindowSize);
                var farPoint = ToLocal(Vec2.FromPolar(midRadius, spans[0].CenterDeg),
                    tokens.Radius.WindowSize);

                overlay.DismissPanelForProbe();
                overlay.PressForProbe(plusPoint.X, plusPoint.Y);
                ProbeOutput.Kv("只按下「＋」", overlay.PanelAttached ? "弹了面板（异常）" : "没弹（正确）");
                overlay.ReleaseForProbe(farPoint.X, farPoint.Y);
                ProbeOutput.Kv("　挪走后再松开", overlay.PanelAttached ? "弹了面板（异常）" : "没弹（正确）");
                results.Add(("「＋」也要完整点击才弹面板",
                    !overlay.PanelAttached, ""));

                overlay.PressForProbe(plusPoint.X, plusPoint.Y);
                overlay.ReleaseForProbe(plusPoint.X, plusPoint.Y);
                ProbeOutput.Kv("　在原处按下又松开", overlay.PanelAttached ? "弹了面板（正确）" : "没弹（异常）");
                results.Add(("「＋」完整点击后正常弹出面板",
                    overlay.PanelAttached, ""));

                overlay.DismissPanelForProbe();
            }

            // ---- 长按 2 秒进入编辑态 + 拖拽重排 --------------------------------
            //
            // 这一段要核的是三件容易出错的事：
            // 1. 长按**不能顺带把程序启动一次** —— 用户想编辑，结果打开了程序；
            // 2. 重排之后**编辑态不能被关掉** —— 编辑态曾经是由"面板是否打开"推导的，
            //    而重排会触发一次菜单重建，那个推导会把编辑态算没；
            // 3. 拖到同一格不该写盘（一次没挪动的拖拽不该产生副作用）。
            ProbeOutput.Section("长按进入编辑态与拖拽重排");

            // 先确保是日常态，并且记下拖拽前的顺序（用菜单签名比对，不看内部字段）
            overlay.SetEditModeForProbe(false);
            await Task.Delay(300);

            var beforeSignature = Hosting.DefaultMenu.Build().ToString();   // 占位，下面换成真实签名
            var orderBefore = menu.Items.Select(i => i.Label).ToList();
            ProbeOutput.Kv("拖拽前的顺序", string.Join("、", orderBefore));

            // 日常态（无「＋」）下第一层的格子角度。多处都要按当前布局重算，
            // 因为切换编辑态会改变槽数（9 ↔ 8），父扇区中心角随之变化。
            var spansNow8 = RadialLayout.Distribute(menu.Items.Count);

            var dragFrom = leafIndex;
            var dragTo = leafIndex >= 2 ? 0 : menu.Items.Count - 1;   // 拖到明显不同的位置

            var fromPoint = ToLocal(Vec2.FromPolar(midRadius, spans[dragFrom].CenterDeg),
                tokens.Radius.WindowSize);
            var toPoint = ToLocal(Vec2.FromPolar(midRadius, spans[dragTo].CenterDeg),
                tokens.Radius.WindowSize);

            ProbeOutput.Kv("长按阈值", overlay.LongPressMsForProbe + " ms");

            // 1) 按住不动，等过长按阈值
            ResetRecorders();
            overlay.PressForProbe(fromPoint.X, fromPoint.Y);

            await Task.Delay(overlay.LongPressMsForProbe + 150);   // 真实等待：这一条没法用假时钟

            var enteredEdit = overlay.RadialStateForProbe.EditMode;
            ProbeOutput.Kv("按住 " + overlay.LongPressMsForProbe + " ms 后是否进入编辑态",
                enteredEdit ? "是" : "否");
            ProbeOutput.Kv("　是否顺带执行了该项", activated is null ? "没有（正确）" : $"执行了 {activated}");

            results.Add(("长按进入编辑态", enteredEdit, $"EditMode={enteredEdit}"));
            results.Add(("长按不执行该项（否则长按变成一次误启动）",
                activated is null, activated?.ToString() ?? "无"));

            // 2) 长按后松手 —— 仍然不该执行
            ResetRecorders();
            overlay.ReleaseForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(200);
            ProbeOutput.Kv("长按后松手是否执行", activated is null ? "没有（正确）" : $"执行了 {activated}");
            results.Add(("长按后松手不执行", activated is null, activated?.ToString() ?? "无"));

            // 3) 编辑态下点击扇区不执行
            ResetRecorders();
            overlay.LeftClickForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(200);
            ProbeOutput.Kv("编辑态下点击扇区是否执行", activated is null ? "没有（正确）" : $"执行了 {activated}");
            results.Add(("编辑态下点击扇区不执行（编辑时误触代价太大）",
                activated is null, activated?.ToString() ?? "无"));

            // 4) 长按后拖到另一格再松手 —— 应当重排
            (int ParentIndex, int From, int To)? reorder = null;
            overlay.ReorderRequested += (_, req) => reorder = req;

            ResetRecorders();
            overlay.PressForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(overlay.LongPressMsForProbe + 150);

            ProbeOutput.Kv("再次长按后正在拖拽", overlay.IsDraggingForProbe ? "是" : "否");
            results.Add(("长按后继续按住就进入拖拽（一个连续手势）",
                overlay.IsDraggingForProbe, ""));

            overlay.MovePointerForProbe(toPoint.X, toPoint.Y);
            await Task.Delay(150);

            ProbeOutput.Kv("　目标格子", overlay.DragTargetSlotForProbe.ToString());
            ProbeOutput.Kv("　目标标记是否画出来了", overlay.DragMarkerAttachedForProbe ? "是" : "否");
            results.Add(("拖拽时目标格子有可见标记",
                overlay.DragMarkerAttachedForProbe, ""));
            results.Add(("落点格子与指针所在扇区一致",
                overlay.DragTargetSlotForProbe == dragTo,
                $"{overlay.DragTargetSlotForProbe} vs {dragTo}"));

            overlay.ReleaseForProbe(toPoint.X, toPoint.Y);
            await Task.Delay(200);

            ProbeOutput.Kv("松手后发出的重排请求", reorder?.ToString() ?? "(没有)");
            results.Add(("松手发出重排请求（含从哪到哪）",
                reorder is { } rq && rq.From == dragFrom && rq.To == dragTo,
                reorder?.ToString() ?? "无"));

            ProbeOutput.Kv("　重排后编辑态是否仍在",
                overlay.RadialStateForProbe.EditMode ? "在（正确）" : "没了（异常）");
            results.Add(("重排后编辑态仍在（曾因重建被算没）",
                overlay.RadialStateForProbe.EditMode, ""));

            // 5) 拖回同一格 —— 不该发出请求
            reorder = null;
            ResetRecorders();
            overlay.PressForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(overlay.LongPressMsForProbe + 150);
            overlay.MovePointerForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(120);
            overlay.ReleaseForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(200);

            ProbeOutput.Kv("拖到同一格后发出的请求", reorder?.ToString() ?? "(没有，正确)");
            results.Add(("拖回同一格不发出重排请求（不产生无意义的写盘）",
                reorder is null, reorder?.ToString() ?? "无"));

            // 5.4) 编辑态下**按下就直接拖拽**，不必再长按 1 秒。
            // 长按的语义是"进入编辑态"；进了之后每次拖拽都还要再按一秒很烦。
            overlay.SetEditModeForProbe(true);
            await Task.Delay(250);

            ResetRecorders();
            overlay.PressForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(120);
            ProbeOutput.Kv("编辑态下按下是否立即进入拖拽",
                overlay.IsDraggingForProbe ? "是（正确）" : "否（还要再长按）");
            results.Add(("编辑态下按下即拖拽，不必再长按",
                overlay.IsDraggingForProbe, ""));
            overlay.ReleaseForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(200);

            // 5.5) 松手之后编辑态必须**仍然在**。
            // 用户反馈"松开鼠标就退出了" —— 行为上它一直在（只有 Esc / 点中心 /
            // 点环外才退），但这一条要能证明，不能靠读代码。
            ResetRecorders();
            overlay.PressForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(overlay.LongPressMsForProbe + 150);
            var afterLongPress = overlay.RadialStateForProbe.EditMode;
            overlay.ReleaseForProbe(fromPoint.X, fromPoint.Y);
            await Task.Delay(250);
            var afterRelease = overlay.RadialStateForProbe.EditMode;

            ProbeOutput.Kv("长按进入编辑态", afterLongPress ? "是" : "否");
            ProbeOutput.Kv("　松手之后是否仍在编辑态", afterRelease ? "是（正确）" : "否（异常）");
            results.Add(("松手之后编辑态保持（只有 Esc/点中心/点环外才退出）",
                afterLongPress && afterRelease,
                $"长按后={afterLongPress}，松手后={afterRelease}"));

            // 5.6) 子环里长按：拖的必须是**子环里的项**，不是一级环
            //
            // 用户实测报的：子环长按后编辑的是一级环。原因是拖拽预览只操作第一层的
            // 扇区数组，于是子环里长按会拖到"同一索引的一级扇区"上。
            // parentIndex / childCount 在后面的"第二层子弧"一节才声明，
            // 这里自己算一份（这一节要排在它前面，因为后面的核对依赖编辑态被重置）。
            var childParent = -1;
            for (var i = 0; i < menu.Items.Count; i++)
                if (menu.Items[i].HasChildren) { childParent = i; break; }

            var childN = childParent >= 0 ? menu.Items[childParent].Children.Count : 0;

            if (childParent >= 0 && childN > 0)
            {
                overlay.SetEditModeForProbe(true);
                await Task.Delay(300);
                overlay.ReopenChildArc(childParent);
                await Task.Delay(400);

                // **这里必须用编辑态（含「＋」）的 spans**，也就是本节开头算的那一份：
                // 此刻编辑态是开着的，第一层有 9 格、父扇区中心在 280°；
                // 而日常态的 8 格布局里它在 315° —— 用错了子弧就整体偏移，
                // 按下的点落到环外，长按根本不会触发（又是一次"坐标跟布局不匹配"）。
                var childSlots = RadialLayout.DistributeWithin(
                    RadialLayout.ChildArc(spans[childParent].CenterDeg, childN,
                        tokens.Interaction.ChildSpanDegrees),
                    childN);
                var childBandMid = tokens.Radius.Ring2.At(0.5);
                var cFrom = ToLocal(Vec2.FromPolar(childBandMid, childSlots[0].CenterDeg),
                    tokens.Radius.WindowSize);
                var cTo = ToLocal(Vec2.FromPolar(childBandMid, childSlots[^1].CenterDeg),
                    tokens.Radius.WindowSize);

                var cHit = overlay.HitTestForProbe(cFrom.X, cFrom.Y);
                ProbeOutput.Kv("　按下点的命中", $"{cHit.Zone}/{cHit.Index}");
                ProbeOutput.Kv("　按下前子弧是否展开", overlay.IsChildArcExpanded ? "是" : "否");
                ProbeOutput.Kv("　按下前面板是否开着", overlay.PanelAttached ? "是" : "否");
                ProbeOutput.Kv("　覆盖层是否可见（轮询在跑的前提）", overlay.IsVisible ? "是" : "否（长按不可能触发）");
                ProbeOutput.Kv("　当前编辑态", overlay.RadialStateForProbe.EditMode ? "开" : "关");

                // 编辑态下**按下就直接拖拽**，所以这里不等长按 —— 也不该等：
                // 等一秒反而会把"编辑态下可以立即拖"这个新行为测漏。
                ResetRecorders();
                reorder = null;
                overlay.PressForProbe(cFrom.X, cFrom.Y);
                ProbeOutput.Kv("　按下时判定的索引 / 是否子环",
                    $"{overlay.PressedIndexForProbe} / {overlay.PressedIsChildForProbe}");
                await Task.Delay(150);

                ProbeOutput.Kv("子环里按下后被拖的是",
                    overlay.DragIsChildForProbe ? "子环里的项" : "一级环的项（异常）");
                results.Add(("编辑态下子环里按下即拖拽，且拖的是子环里的项",
                    overlay.IsDraggingForProbe && overlay.DragIsChildForProbe,
                    $"dragging={overlay.IsDraggingForProbe}，isChild={overlay.DragIsChildForProbe}"));

                ProbeOutput.Kv("　子环的编辑态虚线", overlay.ChildEditOutlineAttached ? "已画出" : "没有");
                results.Add(("子环也有编辑态虚线描边（与一级环同形）",
                    overlay.ChildEditOutlineAttached, ""));

                overlay.MovePointerForProbe(cTo.X, cTo.Y);
                await Task.Delay(150);
                ProbeOutput.Kv("　子环拖拽的落点格子", overlay.DragTargetSlotForProbe.ToString());

                overlay.ReleaseForProbe(cTo.X, cTo.Y);
                await Task.Delay(250);

                ProbeOutput.Kv("　发出的重排请求（父索引应为 " + childParent + "）",
                    reorder?.ToString() ?? "(没有)");
                results.Add(("子环拖拽发出的是子层重排（父索引为该项）",
                    reorder is { } cr && cr.ParentIndex == childParent,
                    reorder?.ToString() ?? "无"));
            }

            // 6) 编辑态下 Esc 只退出编辑态，覆盖层仍在
            overlay.SetEditModeForProbe(true);
            await Task.Delay(200);
            overlay.EscapeForProbe();
            await Task.Delay(300);

            ProbeOutput.Kv("编辑态下按 Esc：编辑态", overlay.RadialStateForProbe.EditMode ? "仍在（异常）" : "已退出");
            ProbeOutput.Kv("　覆盖层", overlay.IsVisible ? "仍在（正确）" : "被关掉了（异常）");
            results.Add(("编辑态下 Esc 只退出编辑态、不关覆盖层",
                !overlay.RadialStateForProbe.EditMode && overlay.IsVisible,
                $"edit={overlay.RadialStateForProbe.EditMode}，visible={overlay.IsVisible}"));

            ProbeOutput.Kv("（对照）拖拽前的顺序", string.Join("、", orderBefore));

            // ---- 真实鼠标消息路径 ---------------------------------------------
            // 上面那些「点 X」走的是把命中结果直接喂给分派函数（ClickForProbe）。
            // 那能验"分派逻辑对不对"，但验不了"真实的一次鼠标按下会不会走到那里"——
            // 用户报的恰恰是后者：点「＋」直接进了文件浏览器、没看到选择面板。
            // 所以这一段改用 PostMessage(WM_LBUTTONDOWN) 打一次真实的鼠标按下。
            ProbeOutput.Section("真实鼠标按下（WM_LBUTTONDOWN）");

            // 「＋」只在编辑态出现，所以这一节要先打开它（不能靠上一节留下的状态）。
            overlay.SetEditModeForProbe(true);
            await Task.Delay(300);

            var realBounds = overlay.RadialPhysicalBounds;
            var realScale = MonitorLayout.AtCursor().Scale;
            if (realBounds is { } rb)
            {
                // 「＋」扇区的中半径处，换算到屏幕物理像素。
                var plusOffset = Vec2.FromPolar(midRadius * realScale, spans[rootAdd].CenterDeg);
                var clickScreenX = (int)Math.Round(rb.X + rb.Width / 2.0 + plusOffset.X);
                var clickScreenY = (int)Math.Round(rb.Y + rb.Height / 2.0 + plusOffset.Y);

                var saved = new { X = 0, Y = 0 };
                PInvoke.GetCursorPos(out var cursorBefore);
                saved = new { X = cursorBefore.X, Y = cursorBefore.Y };

                PInvoke.SetCursorPos(clickScreenX, clickScreenY);
                await Task.Delay(250);

                overlay.DismissPanelForProbe();
                ResetRecorders();

                var hwnd = (HWND)overlay.RadialHandle;
                PInvoke.GetWindowRect(hwnd, out var windowRect);
                var clientX = clickScreenX - windowRect.left;
                var clientY = clickScreenY - windowRect.top;
                var lParam = new LPARAM((clientY << 16) | (clientX & 0xFFFF));

                PInvoke.PostMessage(hwnd, PInvoke.WM_LBUTTONDOWN, new WPARAM(1), lParam);
                // 必须补上抬起：自从改成"完整点击才执行"之后，只有按下不该有任何反应。
                // 这条核对第一次就是靠这个失败的 —— 它证明了新语义在真实鼠标消息路径上也生效。
                PInvoke.PostMessage(hwnd, PInvoke.WM_LBUTTONUP, default, lParam);
                await Task.Delay(400);

                ProbeOutput.Kv("按下位置（屏幕像素）", $"({clickScreenX},{clickScreenY})");
                ProbeOutput.Kv("　按下后面板类型", overlay.PanelKindForProbe);
                ProbeOutput.Kv("　面板给出的选项", string.Join(" / ", overlay.PanelRowLabelsForProbe));
                ProbeOutput.Kv("　是否直接请求了加项", addRequested?.ToString() ?? "否");

                results.Add(("真实鼠标按下「＋」弹出的是选择面板（而不是直接进文件浏览器）",
                    overlay.PanelKindForProbe == "AddChoice" && addRequested is null,
                    $"kind={overlay.PanelKindForProbe}，add={addRequested?.ToString() ?? "无"}"));

                // 面板必须**看得见、点得到**：贴在扇区外缘，不能被裁掉或在窗口外。
                if (overlay.PanelAttached)
                {
                    var panelRect = overlay.PanelBoundsForProbe;
                    ProbeOutput.Kv("　面板在窗口内的位置", panelRect is { } pr
                        ? $"({pr.Left:0},{pr.Top:0}) {pr.Width:0}x{pr.Height:0}"
                        : "(取不到)");
                    ProbeOutput.Kv("　窗口尺寸", $"{tokens.Radius.WindowSize} DIP");

                    var inside = panelRect is { } r
                        && r.Left >= 0 && r.Top >= 0
                        && r.Right <= tokens.Radius.WindowSize
                        && r.Bottom <= tokens.Radius.WindowSize;

                    results.Add(("面板完整落在轮盘窗口内（不会被裁掉）", inside,
                        panelRect?.ToString() ?? "无"));

                    // 第一行按钮中心处的真实落点：必须还是轮盘窗口。
                    // 落在别处就意味着"面板看得见但点不动"。
                    if (panelRect is { } p2)
                    {
                        var rowX = (int)Math.Round(rb.X + (p2.Left + p2.Width / 2) * realScale);
                        var rowY = (int)Math.Round(rb.Y + (p2.Top + 20) * realScale);
                        var hitWindow = PInvoke.WindowFromPoint(new System.Drawing.Point(rowX, rowY));
                        ProbeOutput.Kv("　面板第一行处的落点窗口",
                            hitWindow == hwnd ? "轮盘窗口本身" : $"0x{(nint)hitWindow:X}（不是轮盘）");
                        results.Add(("面板第一行点得到", hitWindow == hwnd,
                            $"0x{(nint)hitWindow:X} vs 0x{(nint)hwnd:X}"));
                    }
                }

                PInvoke.SetCursorPos(saved.X, saved.Y);
                await Task.Delay(150);
            }

            // 选「新建分组…」→ 转入命名输入，确认后发出建分组请求。
            var groupLabel = "探针分组";
            string? groupRequested = null;
            overlay.AddGroupRequested += (_, r) => groupRequested = $"{r.ParentIndex}|{r.Label}";

            ResetRecorders();
            overlay.ClickForProbe(addPoint.X, addPoint.Y);
            var groupRow = overlay.PanelRowLabelsForProbe.ToList().FindIndex(l => l.Contains("新建分组"));
            overlay.InvokePanelRowForProbe(groupRow);

            ProbeOutput.Kv("选「新建分组…」后的面板", overlay.PanelKindForProbe);
            results.Add(("选「新建分组…」转入命名输入",
                overlay.PanelKindForProbe == "TextInput",
                overlay.PanelKindForProbe));

            // 输入框里的默认名字是「新建分组」，确认后应当带着名字发出请求。
            overlay.ConfirmPanelTextForProbe(groupLabel);
            ProbeOutput.Kv("　　确认命名后的请求", groupRequested ?? "(没有)");
            ProbeOutput.Kv("　　是否同时请求了加项", addRequested is null ? "否（对）" : $"是 {addRequested}");
            results.Add(("确认命名后请求新建分组",
                groupRequested == $"-1|{groupLabel}" && addRequested is null,
                groupRequested ?? "无"));

            // ---- 点击真实叶子项 ----------------------------------------------
            // 找一个没有子项的项：它应当是"执行"，而不是"请求加项"。
            var leaf = -1;
            for (var i = 0; i < menu.Items.Count; i++)
                if (!menu.Items[i].HasChildren) { leaf = i; break; }

            if (leaf >= 0)
            {
                var leafPoint = ToLocal(Vec2.FromPolar(midRadius, spans[leaf].CenterDeg),
                    tokens.Radius.WindowSize);

                // 编辑态下点击不执行，所以这一条要先把编辑态关掉；
                // 验完立刻打开 —— 下面的子环要用它的「＋」。
                overlay.SetEditModeForProbe(false);
                await Task.Delay(300);

                ResetRecorders();
                overlay.ClickForProbe(leafPoint.X, leafPoint.Y);

                ProbeOutput.Kv($"点叶子项 #{leaf}（{menu.Items[leaf].Label}）→ ItemActivated",
                    activated?.ToString() ?? "(没有)");
                ProbeOutput.Kv("　同时请求了加项", addRequested is null ? "否" : $"是 {addRequested}");
                results.Add(("点叶子项是执行而不是加项",
                    activated is { Child: -1 } && activated.Value.Parent == leaf && addRequested is null,
                    $"activated={activated?.ToString() ?? "无"}，add={addRequested?.ToString() ?? "无"}"));

                overlay.SetEditModeForProbe(true);
                await Task.Delay(300);
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

                // 子环的「＋」只在编辑态出现，所以要显式打开（不能靠上一节留下的状态）。
                overlay.SetEditModeForProbe(true);
                await Task.Delay(300);

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

                    // 子环的「＋」必须**先出面板**，不能直接进文件浏览器。
                    // 用户报的就是"点＋号直接打开文件浏览器、看不到新建分组" ——
                    // 两个「＋」行为不一致正是那条的来源，所以这条核对要一直留着。
                    ProbeOutput.Kv("点子弧「＋」弹出的面板", overlay.PanelKindForProbe);
                    ProbeOutput.Kv("　面板给出的选项", string.Join(" / ", overlay.PanelRowLabelsForProbe));
                    ProbeOutput.Kv("　是否直接请求了加项（应当还没）", addRequested?.ToString() ?? "没有（对）");

                    results.Add(("点子环「＋」先出面板、不直接进文件浏览器",
                        overlay.PanelKindForProbe == "AddChoice" && addRequested is null,
                        $"kind={overlay.PanelKindForProbe}，add={addRequested?.ToString() ?? "无"}"));

                    var childProgramRow = overlay.PanelRowLabelsForProbe.ToList()
                        .FindIndex(l => l.Contains("添加程序"));
                    overlay.InvokePanelRowForProbe(childProgramRow);

                    ProbeOutput.Kv("　选「添加程序…」后 → AddItemRequested",
                        addRequested?.ToString() ?? "(没有)");
                    ProbeOutput.Kv("　同时触发了 ItemActivated", activated is null ? "否" : $"是 {activated}");
                    results.Add(("子环面板选「添加程序…」后请求加进该父项",
                        addRequested == parentIndex && activated is null,
                        $"parent={addRequested?.ToString() ?? "无"}，期望 {parentIndex}"));
                }

                // 这里**刻意不再**单独核"子项被点击时执行"。
                //
                // 编辑态会抑制执行（编辑时误触启动程序的代价太大），所以核这一条就得
                // 中途把编辑态关掉 —— 而关掉编辑态会改变第一层的槽数（「＋」那格没了），
                // 于是**整节预先算好的角度全部作废**：子弧位置变了，点击落到环外，
                // 而"点环外"的语义是关闭覆盖层，后面整片核对跟着全红。
                //
                // 而这条核对本身是冗余的：抑制执行走的是同一段 `!_editMode` 判断，
                // 第一层那条"编辑态下点击扇区不执行"已经覆盖了它。
                // 用一条冗余核对把整节状态搅乱不值得 —— 核对的代价也是成本。

                overlay.CollapseChildArcForProbe();
                await Task.Delay(400);
                ProbeOutput.Kv("收起子弧后的鼓出量", $"{overlay.CurrentChildBulgeForProbe:0.##} px");
                results.Add(("收起子弧后鼓出归零",
                    overlay.CurrentChildBulgeForProbe == 0,
                    $"{overlay.CurrentChildBulgeForProbe:0.##} px"));
            }

            // ---- 子环容量：铺满整圈 ------------------------------------------
            // 用户报的："外圈子环的选项数量上限不能只有 12 个，至少要能围成完整一圈"。
            // 这里用一个 16 子项的菜单核对：弧跨度到 360°、每个子项命中自己、
            // 并且「＋」在编辑态下还留得住（16 < 20）。
            ProbeOutput.Section("子环容量（16 个子项）");
            const int manyChildren = 16;
            var wide = menu with
            {
                Items = menu.Items.Take(menu.Items.Count - 1).Append(menu.Items[^1] with
                {
                    Children = Enumerable.Range(1, manyChildren)
                        .Select(i => new MenuItemSpec { Label = $"子项 {i}" })
                        .ToList(),
                }).ToList(),
            };

            overlay.RebuildForMenu(wide);
            overlay.SetEditModeForProbe(true);
            await Task.Delay(600);

            overlay.ExpandChildArcForProbe(wide.Items.Count - 1);
            await Task.Delay(400);

            var wideParent = wide.Items.Count - 1;
            var parentCenter = RadialLayout.Distribute(wide.Items.Count + 1)[wideParent].CenterDeg;
            var wideArc = RadialLayout.ChildArc(
                parentCenter, manyChildren + 1, tokens.Interaction.ChildSpanDegrees);
            var wideSpan = RadialLayout.DistributeWithin(wideArc, manyChildren + 1);

            ProbeOutput.Kv("父项中心角", $"{parentCenter:0.#}°");
            ProbeOutput.Kv("子弧跨度", $"{wideArc.SweepDeg:0.#}°（整圈 360° 表示已铺满）");

            results.Add(($"{manyChildren} 个子项时子弧铺满整圈",
                Math.Abs(wideArc.SweepDeg - 360) < 0.001,
                $"{wideArc.SweepDeg:0.#}°"));

            var wideMisses = new List<string>();
            var wideMid = tokens.Radius.Ring2.At(0.5);
            for (var i = 0; i < wideSpan.Length; i++)
            {
                var local = ToLocal(Vec2.FromPolar(wideMid, wideSpan[i].CenterDeg), tokens.Radius.WindowSize);
                var hit = overlay.HitTestForProbe(local.X, local.Y);
                if (hit.Zone != HitZone.ChildSector || hit.Index != i)
                    wideMisses.Add($"子扇区{i}（{wideSpan[i].CenterDeg:0.#}°）→ {hit.Zone}/{hit.Index}");
            }

            ProbeOutput.Kv("逐项命中核对", wideMisses.Count == 0
                ? $"{wideSpan.Length} 项全部命中自己"
                : string.Join("；", wideMisses));
            results.Add(("整圈子环每项都命中自己", wideMisses.Count == 0,
                wideMisses.Count == 0 ? $"{wideSpan.Length} 项" : string.Join("；", wideMisses)));

            ProbeOutput.Kv("子弧「＋」索引", overlay.ChildAddSectorIndexForProbe.ToString());
            results.Add(("整圈子环里「＋」仍留得住",
                overlay.ChildAddSectorIndexForProbe == manyChildren,
                $"索引 {overlay.ChildAddSectorIndexForProbe}，期望 {manyChildren}"));

            overlay.CollapseChildArcForProbe();
            await Task.Delay(300);
            overlay.RebuildForMenu(menu);
            overlay.SetEditModeForProbe(true);
            await Task.Delay(600);

            // ---- 右键扇区：编辑面板 ------------------------------------------
            ProbeOutput.Section("右键扇区（改名 / 移除）");

            // 面板必须真的在视觉树上 —— 只报"开了"是内部状态自证，
            // 之前有过"对象建好但没接进视觉树"的先例。
            var sectorPoint = ToLocal(Vec2.FromPolar(midRadius, spans[2].CenterDeg),
                tokens.Radius.WindowSize);
            var rHit = overlay.RightClickForProbe(sectorPoint.X, sectorPoint.Y);
            ProbeOutput.Kv("右键扇区 #2 的命中", $"{rHit.Zone}/{rHit.Index}");
            ProbeOutput.Kv("  面板已打开且已挂到视觉树", overlay.IsPanelOpen ? "是" : "否");
            ProbeOutput.Kv("  面板指向的项", overlay.EditTargetForProbe.ToString());

            results.Add(("右键扇区会弹出编辑面板",
                overlay.IsPanelOpen && overlay.EditTargetForProbe == (2, -1),
                $"open={overlay.IsPanelOpen}，target={overlay.EditTargetForProbe}"));

            // 面板开着时，左键的第一下只收面板，不能顺手执行下面的项 ——
            // 面板通常正好压在某个扇区上，误执行就是"想改名却启动了程序"。
            ResetRecorders();
            overlay.LeftClickForProbe(sectorPoint.X, sectorPoint.Y);
            ProbeOutput.Kv("面板开着时左键点扇区", activated is null && addRequested is null
                ? "只收起了面板（正确）" : $"触发了动作 {activated}{addRequested}");
            ProbeOutput.Kv("  面板已收起", overlay.IsPanelOpen ? "否（异常）" : "是");
            results.Add(("面板开着时左键只收面板、不执行下面的项",
                !overlay.IsPanelOpen && activated is null && addRequested is null,
                $"open={overlay.IsPanelOpen}，activated={activated?.ToString() ?? "无"}"));

            // ---- 加一项之后：窗口本身不动 ------------------------------------
            // 这一节对应用户报的"每次修改快捷选项工具都要重新打开一次"。
            // 判据用**窗口句柄**：句柄不变就证明窗口没有被销毁重建。
            // 这比"看起来没闪"可靠得多 —— 后者取决于机器快慢，换台机器就未必成立。
            ProbeOutput.Section("加一项之后（就地刷新，不重开窗口）");
            var beforeBounds = overlay.RadialPhysicalBounds;
            var beforeHandle = overlay.RadialHandle;
            ProbeOutput.Kv("刷新前轮盘矩形", beforeBounds?.ToString() ?? "(不可见)");
            ProbeOutput.Kv("刷新前窗口句柄", $"0x{beforeHandle:X}");

            var grown = menu with
            {
                Items = menu.Items.Append(new MenuItemSpec
                {
                    Label = "探针新增项",
                    Action = MenuAction.Launch("cmd.exe"),
                }).ToList(),
            };

            overlay.RebuildForMenu(grown);
            // 正式路径里这次重建会把编辑态对齐到"设置面板是不是开着"，探针没开面板，
            // 所以要自己再按上 —— 这就是"面板开着"在探针里的等价物。
            overlay.SetEditModeForProbe(true);
            await Task.Delay(700);

            var afterBounds = overlay.RadialPhysicalBounds;
            var afterHandle = overlay.RadialHandle;
            ProbeOutput.Kv("刷新后轮盘矩形", afterBounds?.ToString() ?? "(不可见)");
            ProbeOutput.Kv("刷新后窗口句柄", $"0x{afterHandle:X}");
            ProbeOutput.Kv("覆盖层是否仍然可见", overlay.IsVisible ? "是" : "否（异常）");
            ProbeOutput.Kv("新「＋」索引", overlay.AddSectorIndexForProbe.ToString());

            // 句柄不变 = 窗口没被销毁重建 = 没有重开、没有重播入场动画。
            results.Add(("刷新菜单不重开窗口（句柄不变）",
                beforeHandle == afterHandle && beforeHandle != 0,
                $"0x{beforeHandle:X} → 0x{afterHandle:X}"));

            results.Add(("刷新菜单期间覆盖层始终可见", overlay.IsVisible, ""));

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

            // ---- 换树时逐帧回调不残留 ----------------------------------------
            // CompositionTarget.Rendering 是静态事件：旧视觉树上的动画器若在动画进行中
            // 就被丢掉，它的订阅会一直留着，整棵旧树既不回收、还每帧都在跑。
            // 换菜单恰恰发生在用户正在操作轮盘的时候，所以这条必须核。
            ProbeOutput.Section("换树时的逐帧回调");
            ProbeOutput.Kv("换树前活跃回调数", AnimatorHeadroom());

            // 先制造一次形变动画，再换树。
            //
            // 这里刻意**不等**：形变只有 130ms，而探针里的 Task.Delay 会因为调度器忙
            // 而超时很远 —— 等 30ms 实际可能过了 200ms，动画早播完、回调也摘干净了，
            // 于是核对变成"没抓到"而不是"没问题"（第一版就是这样时好时坏）。
            // 挂回调是同步发生的，所以同步读就是确定的。
            overlay.HoverForProbe(2);
            ProbeOutput.Kv("悬停后活跃回调数", AnimatorHeadroom());

            var duringHover = SectorBulgeAnimator.ActiveHookCount + ChildArcAnimator.ActiveHookCount;

            overlay.RebuildForMenu(grown);

            var afterSwap = SectorBulgeAnimator.ActiveHookCount + ChildArcAnimator.ActiveHookCount;
            ProbeOutput.Kv("换树后活跃回调数", afterSwap.ToString());
            ProbeOutput.Kv("　（换树时确实有动画在播）", duringHover > 0 ? "是" : "否（本次没抓到）");

            results.Add(("换树后没有残留的逐帧回调",
                duringHover > 0 && afterSwap == 0,
                $"动画中 {duringHover} → 换树后 {afterSwap}"));

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
            overlay.SetEditModeForProbe(true);
            await Task.Delay(700);

            ProbeOutput.Kv("12 项时的「＋」索引", overlay.AddSectorIndexForProbe.ToString());
            ProbeOutput.Kv("12 项时的渲染扇区数（应仍能命中第 12 项）",
                DescribeSectorHit(overlay, full, tokens));

            results.Add(("菜单满 12 项时不再显示「＋」",
                overlay.AddSectorIndexForProbe == -1,
                $"索引 {overlay.AddSectorIndexForProbe}"));

            // ---- Esc / 右键「＋」：优先级与取消语义 ---------------------------
            // 这两条都会（在 Esc 那条里是"不该"）让覆盖层关闭，所以放在最后 ——
            // 前面所有核对都需要它可见。
            ProbeOutput.Section("Esc 的优先级");

            overlay.ShowSectorEditForProbe(1, -1);
            overlay.EscapeForProbe();
            await Task.Delay(400);
            ProbeOutput.Kv("Esc（面板开着）", overlay.IsPanelOpen ? "面板仍在（异常）" : "收起了面板");
            ProbeOutput.Kv("  覆盖层是否被顺手关掉", overlay.IsVisible ? "否（正确）" : "是（异常）");
            results.Add(("Esc 先收编辑面板、不关闭覆盖层",
                !overlay.IsPanelOpen && overlay.IsVisible,
                $"panel={overlay.IsPanelOpen}，visible={overlay.IsVisible}"));

            // 右键「＋」：那里没有可编辑的项，应当当作取消。
            //
            // 注意必须先恢复成 8 项菜单 —— 上面那节把菜单换成了 12 项，
            // 那种菜单里**没有「＋」**，这个坐标落到的是一个真实项。
            // 第一版就踩了这个坑：探针报"右键「＋」弹了面板"，看代码怎么看都该拦住，
            // 实际是探针自己在测一个不存在的「＋」。
            overlay.RebuildForMenu(menu);
            overlay.SetEditModeForProbe(true);
            await Task.Delay(700);
            ProbeOutput.Kv("恢复 8 项菜单后的「＋」索引", overlay.AddSectorIndexForProbe.ToString());
            results.Add(("恢复菜单后「＋」重新出现",
                overlay.AddSectorIndexForProbe == menu.Items.Count,
                $"索引 {overlay.AddSectorIndexForProbe}"));

            overlay.RightClickForProbe(addPoint.X, addPoint.Y);
            ProbeOutput.Kv("右键「＋」扇区是否弹面板", overlay.IsPanelOpen ? "弹了（异常）" : "没弹（正确）");

            await Task.Delay(500);
            ProbeOutput.Kv("  覆盖层是否被关掉", overlay.IsVisible ? "否（异常）" : "是（当作取消）");

            results.Add(("右键「＋」不弹面板、按取消处理",
                !overlay.IsPanelOpen && !overlay.IsVisible,
                $"panel={overlay.IsPanelOpen}，visible={overlay.IsVisible}"));

            // ---- 用户的实际路径：先开设置，再点「＋」 ------------------------
            // 上面所有核对都用 SetEditModeForProbe 造出编辑态，而用户是按
            // 左上角「设置」进编辑态的。这两者只差一件事：**设置面板开着**。
            // 用户报的就是这条路径下点「＋」直接进了文件浏览器 ——
            // 所以必须照着走一遍，而不是继续用探针后门。
            ProbeOutput.Section("用户路径：打开设置后点「＋」");

            // 这一段挪到了最后，而上面"右键「＋」按取消处理"那一条会把覆盖层关掉，
            // 所以先重新显示 —— OpenSettings 在覆盖层不可见时是空操作。
            overlay.Show();
            await Task.Delay(700);

            // 起点必须干净。上面几节可能把设置面板留在打开状态，而 OpenSettings
            // 在"面板已开"时是空操作（也就不会去按编辑态）—— 那这一段就测了个寂寞。
            if (overlay.IsSettingsOpen)
            {
                overlay.CloseSettings();
                await Task.Delay(500);
            }

            // 编辑态也要显式关掉：它现在不随面板关闭而退出。
            overlay.SetEditModeForProbe(false);
            await Task.Delay(300);

            ProbeOutput.Kv("起点：设置未开时的「＋」索引",
                overlay.AddSectorIndexForProbe.ToString());
            results.Add(("起点干净：设置未开时没有「＋」",
                overlay.AddSectorIndexForProbe == -1,
                overlay.AddSectorIndexForProbe.ToString()));

            // 完全照用户的路径：点左上角「设置」。
            overlay.OpenSettings();
            await Task.Delay(800);

            ProbeOutput.Kv("按「设置」后的面板状态", overlay.IsSettingsOpen ? "已打开" : "未打开");
            ProbeOutput.Kv("　「＋」扇区索引", overlay.AddSectorIndexForProbe.ToString());
            ProbeOutput.Kv("　（编辑态 / 菜单项数 / 「＋」索引）", overlay.RadialStateForProbe.ToString());
            results.Add(("按「设置」后「＋」出现（用户路径的第一步）",
                overlay.IsSettingsOpen && overlay.AddSectorIndexForProbe >= 0,
                $"面板={overlay.IsSettingsOpen}，索引={overlay.AddSectorIndexForProbe}"));

            if (overlay.PanelBoundsForProbe is { } settingsRect)
                ProbeOutput.Kv("　（上一次的面板矩形，供对照）", settingsRect.ToString());

            overlay.DismissPanelForProbe();
            ResetRecorders();

            var boundsAfterSettings = overlay.RadialPhysicalBounds;
            var scaleAfterSettings = MonitorLayout.AtCursor().Scale;
            if (boundsAfterSettings is { } sb)
            {
                var offset = Vec2.FromPolar(
                    midRadius * scaleAfterSettings,
                    RadialLayout.Distribute(menu.Items.Count + 1)[rootAdd].CenterDeg);

                var sx = (int)Math.Round(sb.X + sb.Width / 2.0 + offset.X);
                var sy = (int)Math.Round(sb.Y + sb.Height / 2.0 + offset.Y);

                PInvoke.SetCursorPos(sx, sy);
                await Task.Delay(250);

                var hwnd2 = (HWND)overlay.RadialHandle;
                PInvoke.GetWindowRect(hwnd2, out var wr2);

                ProbeOutput.Kv("　「＋」中心处的落点窗口",
                    PInvoke.WindowFromPoint(new System.Drawing.Point(sx, sy)) == hwnd2
                        ? "轮盘窗口本身" : "被别人拿走");

                var lp = new LPARAM(((sy - wr2.top) << 16) | ((sx - wr2.left) & 0xFFFF));
                PInvoke.PostMessage(hwnd2, PInvoke.WM_LBUTTONDOWN, new WPARAM(1), lp);
                PInvoke.PostMessage(hwnd2, PInvoke.WM_LBUTTONUP, default, lp);
                await Task.Delay(500);

                ProbeOutput.Kv("　按下后面板类型", overlay.PanelKindForProbe);
                ProbeOutput.Kv("　面板给出的选项", string.Join(" / ", overlay.PanelRowLabelsForProbe));
                ProbeOutput.Kv("　是否直接请求了加项", addRequested?.ToString() ?? "否");

                results.Add(("开着设置面板时点「＋」也弹选择面板",
                    overlay.PanelKindForProbe == "AddChoice" && addRequested is null,
                    $"kind={overlay.PanelKindForProbe}，add={addRequested?.ToString() ?? "无"}"));
            }

            overlay.DismissPanelForProbe();
            overlay.CloseSettings();
            await Task.Delay(500);
            overlay.SetEditModeForProbe(true);
            await Task.Delay(300);

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

    /// <summary>当前两个动画器挂着的逐帧回调数，拼成一行便于打印。</summary>
    private static string AnimatorHeadroom() =>
        $"形变 {SectorBulgeAnimator.ActiveHookCount} + 子弧 {ChildArcAnimator.ActiveHookCount}";

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
