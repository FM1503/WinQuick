using System.Windows;
using WinQuick.App.Diagnostics;
using WinQuick.App.Design;
using WinQuick.App.Hosting;
using WinQuick.Core.Config;
using Windows.Win32;

namespace WinQuick.App.M0;

/// <summary>
/// 标签探针 —— 核对「长文本被裁剪」与「悬停时文字位移」这两件事。
///
/// 位移那条的判据是：**悬停不改变任何影响排版的属性**。
/// 原来是悬停时把字重从 Normal 换成 SemiBold（更宽 → 整段字往右挪），
/// 再加上一条 Forever 的来回滚动动画（文字持续左右滑）。
/// 两者都会让文字在指针悬停期间移动，而菜单的用法是「看一眼就要点」。
///
/// 所以这里不比对"看起来有没有动"（那要靠眼睛），而是把排版相关的属性逐个比一遍：
/// 字重、字号、折行方式、容器位置、文字宽度。只要有一个变了，文字就可能移动。
///
/// 用法：--label-probe
/// </summary>
public static class LabelProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("label", "m4");
        ProbeOutput.Line("核对长标签的裁剪方式，以及悬停是否会让文字位移。");

        var results = new List<(string Name, bool Ok, string Detail)>();

        OverlayController? overlay = null;
        try
        {
            // 一个不可断行的超长标签：中文能任意断行，只有英文长词才会真的放不下，
            // 所以这里刻意用英文 —— 否则测的是一个走不到的路径。
            const string longLabel = "VeryLongApplicationNameWithoutAnySpaces.exe";
            const string shortLabel = "记事本";

            var baseMenu = DefaultMenu.Build();
            var items = baseMenu.Items.ToList();
            items[0] = items[0] with { Label = longLabel };
            items[1] = items[1] with { Label = shortLabel };
            var menu = baseMenu with { Items = items };

            overlay = new OverlayController(menu);
            overlay.Prewarm();
            overlay.Show();
            await Task.Delay(700);

            ProbeOutput.Section("长标签的处理");
            var longCount = overlay.ElidedLabelCountForProbe;
            ProbeOutput.Kv("被截断的标签数", longCount.ToString());
            ProbeOutput.Kv("最长溢出", $"{overlay.MaxLabelOverflowForProbe:0.#} px");
            ProbeOutput.Kv("完整文字", longLabel);

            results.Add(("超长不可断行的标签会被截断而不是溢出到相邻扇区",
                longCount >= 1, $"截断 {longCount} 个"));

            // ---- 悬停不位移 --------------------------------------------------
            ProbeOutput.Section("悬停是否让文字位移");

            var before = overlay.LabelLayoutSnapshotForProbe(0);
            overlay.HoverForProbe(0);
            await Task.Delay(200);
            var after = overlay.LabelLayoutSnapshotForProbe(0);

            ProbeOutput.Kv("悬停前（字重/字号/宽度/容器左边缘）", before.ToString());
            ProbeOutput.Kv("悬停后", after.ToString());
            ProbeOutput.Kv("排版属性是否全部未变", before == after ? "是" : "否");

            results.Add(("悬停不改变任何影响排版的属性（文字不位移）",
                before == after,
                $"{before} → {after}"));

            // 悬停原来的第二处位移来源：滚动动画。现在不该再有任何动画时钟。
            ProbeOutput.Kv("标签上是否还挂着动画", overlay.LabelHasAnimationForProbe(0) ? "有（异常）" : "没有");
            results.Add(("悬停不再启动滚动动画",
                !overlay.LabelHasAnimationForProbe(0), ""));

            // ---- 中心读出 ----------------------------------------------------
            // **必须用真实指针**：轮盘的指针轮询每 25ms 用"指针实际在哪"重算一次悬停，
            // 探针直接调 SetHover 的话，下一次轮询就把它清掉了 ——
            // 读到的是"没悬停"，而不是程序不对。（第一版就是这么误报的。）
            ProbeOutput.Section("中心显示完整文字");

            var bounds = overlay.RadialPhysicalBounds;
            var scale = MonitorLayout.AtCursor().Scale;
            if (bounds is { } rect)
            {
                var offset = Core.Geometry.Vec2.FromPolar(
                    TokenStore.Current.Radius.LabelRadius * scale, 0);
                PInvoke.SetCursorPos(
                    (int)Math.Round(rect.X + rect.Width / 2.0 + offset.X),
                    (int)Math.Round(rect.Y + rect.Height / 2.0 + offset.Y));
                await Task.Delay(600);      // 悬停延迟 200ms，留足余量
            }

            ProbeOutput.Kv("中心读出可见", overlay.IsCenterReadoutVisibleForProbe ? "是" : "否");
            ProbeOutput.Kv("中心读出的文字", overlay.CenterReadoutTextForProbe);

            results.Add(("悬停被截断项时中心显示完整文字",
                overlay.IsCenterReadoutVisibleForProbe
                && overlay.CenterReadoutTextForProbe == longLabel,
                overlay.CenterReadoutTextForProbe));

            // 没被截断的项不该占用中心 —— 那里同时是 "Esc" 提示的位置。
            if (bounds is { } r2)
            {
                var offset = Core.Geometry.Vec2.FromPolar(TokenStore.Current.Radius.LabelRadius * scale, 45);
                PInvoke.SetCursorPos(
                    (int)Math.Round(r2.X + r2.Width / 2.0 + offset.X),
                    (int)Math.Round(r2.Y + r2.Height / 2.0 + offset.Y));
            }
            await Task.Delay(600);
            ProbeOutput.Kv("悬停短标签项时中心读出", overlay.IsCenterReadoutVisibleForProbe ? "可见（异常）" : "隐藏（正确）");
            ProbeOutput.Kv("　被悬停的短标签", overlay.LabelTextForProbe(1));

            results.Add(("没被截断的项不占用中心（Esc 提示仍可见）",
                !overlay.IsCenterReadoutVisibleForProbe,
                $"label={overlay.LabelTextForProbe(1)}"));

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
