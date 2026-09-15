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
            // 要**足够长**才能真的触发截断：放宽到三行之后，能容纳的文字大约翻了一倍，
            // 原来那条 43 字符的名字现在装得下了（那正是想要的效果）。
            // 这里留一条真的放不下的，用来守住"截断"这条兜底路径。
            const string longLabel = "VeryLongApplicationNameWithoutAnySpacesAndEvenMoreTextToOverflow.exe";
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

            results.Add(("真正放不下的标签会被明确截断（带省略号），而不是溢出",
                longCount >= 1, $"截断 {longCount} 个"));

            // 最重要的不变量：**没有任何标签是"既没装下、又没标出截断"的**。
            // 那种状态就是"少了一截还没有提示"，也是最初的缺陷形态。
            var clipped = overlay.SilentlyClippedLabelsForProbe;
            ProbeOutput.Kv("被悄悄裁掉的标签数", clipped.Count.ToString());
            ProbeOutput.Kv("　（既没装下、又没标出截断的）",
                clipped.Count == 0 ? "没有" : string.Join("；", clipped));

            results.Add(("没有标签被悄悄裁掉（要么装下、要么明确截断）",
                clipped.Count == 0, clipped.Count == 0 ? "0 个" : string.Join("；", clipped)));

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

            // ---- Esc 提示不被顶掉 --------------------------------------------
            // 曾经有一版会在悬停到被截断的项时，把中心的 "Esc" 换成完整名字。
            // 那是错的：Esc 是界面上唯一的取消提示，不该因为"某个项名字长"就消失。
            // 而且那个做法本身也做不到它承诺的事 —— 中心只有 112px 宽，
            // 长名字在那里同样要截断，等于把同一个缺陷画了两遍。
            //
            // **必须用真实指针**：轮盘的指针轮询每 25ms 用"指针实际在哪"重算一次悬停，
            // 探针直接调 SetHover 的话下一次轮询就把它清掉了。
            ProbeOutput.Section("Esc 提示在整个悬停过程中都不消失");

            var bounds = overlay.RadialPhysicalBounds;
            var scale = MonitorLayout.AtCursor().Scale;

            async Task HoverAtAsync(double angleDeg)
            {
                if (bounds is not { } rect) return;

                var offset = Core.Geometry.Vec2.FromPolar(
                    TokenStore.Current.Radius.LabelRadius * scale, angleDeg);
                PInvoke.SetCursorPos(
                    (int)Math.Round(rect.X + rect.Width / 2.0 + offset.X),
                    (int)Math.Round(rect.Y + rect.Height / 2.0 + offset.Y));
                await Task.Delay(600);      // 悬停延迟 200ms，留足余量
            }

            await HoverAtAsync(0);          // 被截断的长标签
            ProbeOutput.Kv("悬停被截断项时", overlay.EscHintVisibleForProbe ? "Esc 可见（正确）" : "Esc 消失（异常）");
            results.Add(("悬停被截断的长标签时 Esc 提示仍在",
                overlay.EscHintVisibleForProbe, $"{longLabel}"));

            await HoverAtAsync(45);         // 短标签
            ProbeOutput.Kv("悬停短标签项时", overlay.EscHintVisibleForProbe ? "Esc 可见（正确）" : "Esc 消失（异常）");
            ProbeOutput.Kv("　被悬停的短标签", overlay.LabelTextForProbe(1));
            results.Add(("悬停短标签时 Esc 提示仍在",
                overlay.EscHintVisibleForProbe, overlay.LabelTextForProbe(1)));

            // ---- 常见的长名字不该被截断 --------------------------------------
            // "Adobe Photoshop 2025" 这种真实名字此前会被截断成 "Adobe Phot…"，
            // 原因是只允许折两行，而它需要三行（Adobe / Photoshop / 2025）。
            // 环内的径向空间本来就是够的（三行 41px，落在 129~170，环是 56~200），
            // 所以放宽到三行比"截断 + 想办法把完整名字显示到别处"更直接。
            ProbeOutput.Section("常见的长名字是否还要截断");

            var commonItems = baseMenu.Items.ToList();
            commonItems[2] = commonItems[2] with { Label = "Adobe Photoshop 2025" };
            overlay.RebuildForMenu(baseMenu with { Items = commonItems });
            await Task.Delay(600);

            ProbeOutput.Kv("「Adobe Photoshop 2025」是否被截断",
                overlay.LabelIsElidedForProbe(2) ? "是" : "否（完整显示）");
            ProbeOutput.Kv("　最终字号 / 占宽",
                $"{overlay.LabelFontSizeForProbe(2):0.#} / {overlay.LabelTextWidthForProbe(2):0.#}");

            results.Add(("常见的长名字（Adobe Photoshop 2025）能完整显示、不截断",
                !overlay.LabelIsElidedForProbe(2),
                $"elided={overlay.LabelIsElidedForProbe(2)}，字号={overlay.LabelFontSizeForProbe(2):0.#}"));

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
