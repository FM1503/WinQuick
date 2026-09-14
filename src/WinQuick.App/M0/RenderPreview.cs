using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using IoDirectory = System.IO.Directory;
using IoFile = System.IO.File;
using IoPath = System.IO.Path;

namespace WinQuick.App.M0;

/// <summary>
/// 离屏渲染探针 —— 视觉质量验证机制。
///
/// 背景：驱动本项目的模型**不支持图像输入**，图会被直接丢弃，
/// 所以「渲染成 PNG 再看一眼」这条反馈回环对它不成立。
/// 替代方案是把渲染结果变成文本：本探针产出 PNG，
/// 再由 tools/analyze-render.ps1 做极坐标采样并输出纯文本。
/// 它能抓住几何与配色错误（角度、半径、透明、颜色、图标是否画出来），抓不住审美。
///
/// 关键：它调用的是 <see cref="RadialRingView"/>，与运行时窗口用的是同一个构造函数。
/// 否则「预览好看、实际运行不一样」这类分叉会一直存在，验证也就失去意义了。
/// </summary>
public static class RenderPreview
{
    public static void Run(CliOptions cli)
    {
        ProbeOutput.Initialize("render-preview", "m3");

        var outPath = cli.GetString("out",
            IoPath.Combine(Paths.RepoRoot, "artifacts", "m2", "preview.png"));
        var state = cli.GetString("state", "default");
        var menu = MenuSpec.BuiltInDefault;
        var count = Math.Min(cli.GetInt("sectors", menu.Items.Count), menu.Items.Count);

        // 只取前 N 项，便于渲染 4/6/8 扇区等不同几何做验证。
        var trimmed = menu with { Items = menu.Items.Take(count).ToList() };

        // --stress-labels：把标签替换成长文本，用来验证「自动缩小字号」和「悬停滚动」
        // 这两条兜底逻辑是否真的会生效。不验的话它们就是没跑过的代码。
        if (cli.Has("stress-labels"))
        {
            trimmed = trimmed with
            {
                Items = trimmed.Items.Select(i => i with
                {
                    Label = i.Label + "（超长名称压力测试）",
                }).ToList(),
            };
            ProbeOutput.Line("已启用压力标签：全部标签追加长后缀");
        }

        // child / child-leaf 两个状态用来验证子弧（第二层环）。
        // 它们把子弧立即落定到展开态 —— 只渲染一帧，动画推进不了，
        // 否则画出来的永远是未展开的形态，子弧就等于没被验证过。
        var wantsChildArc = state is "child" or "child-leaf";
        var parentWithChildren = -1;
        for (var i = 0; i < trimmed.Items.Count; i++)
            if (trimmed.Items[i].HasChildren) { parentWithChildren = i; break; }

        var hoverIndex = state switch
        {
            "hover" => Math.Min(2, count - 1),
            "hover-first" => 0,
            "hover-last" => count - 1,
            "child" or "child-leaf" => parentWithChildren,
            _ => -1,
        };

        ProbeOutput.Line($"状态: {state}（扇区数 {count}，悬停索引 {(hoverIndex < 0 ? "无" : hoverIndex)}）");

        try
        {
            var tokens = TokenStore.Current;
            var view = RadialRingView.Build(trimmed);
            // 用立即落定而不是 SetHover：只渲染一帧，动画推进不了，
            // 否则画出来的永远是未形变状态，形变就等于没被验证过。
            view.SetHoverImmediate(hoverIndex);

            if (wantsChildArc && parentWithChildren >= 0)
            {
                view.ExpandChildArc(parentWithChildren, immediate: true);
                // child-leaf 额外高亮第一个子项，用来验证子弧的悬停高亮色。
                if (state == "child-leaf") view.SetChildHover(0);
            }

            var size = tokens.Radius.WindowSize;
            var root = view.Root;
            root.Measure(new Size(size, size));
            root.Arrange(new Rect(0, 0, size, size));
            root.UpdateLayout();

            // size 是 int（来自 tokens），Math.Ceiling 在 int 上会与 decimal 重载产生二义性。
            var pixelSize = (int)Math.Ceiling((double)size);
            var bitmap = new RenderTargetBitmap(
                pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();

            var dir = IoPath.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) IoDirectory.CreateDirectory(dir);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = IoFile.Create(outPath))
                encoder.Save(fs);

            ProbeOutput.Kv("输出", outPath);
            ProbeOutput.Kv("尺寸", $"{bitmap.PixelWidth} x {bitmap.PixelHeight}");
            ProbeOutput.Section("渲染内容核对");
            ProbeOutput.Kv("菜单项数", count);
            ProbeOutput.Kv("成功取到图标的项", $"{view.RenderedIconCount} / {count}");
            ProbeOutput.Kv("有子菜单的项（应画指示标记）", view.ChildMarkerCount);
            ProbeOutput.Kv("标签可用宽度", $"{AvailableWidthFor(tokens, count):0.#} DIP");
            ProbeOutput.Kv("需要滚动才能看全的标签", view.ScrollNeededCount);
            ProbeOutput.Kv("被自动缩小过字号的标签", view.ShrunkLabelCount);
            ProbeOutput.Kv("发生折行的标签", view.WrappedLabelCount);
            ProbeOutput.Kv("悬停扇区外扩", $"{view.CurrentHoverBulge:0.#} px");
            ProbeOutput.Kv("子弧已展开", view.IsChildArcVisible ? $"是（父索引 {view.ChildArcParent}）" : "否");
            ProbeOutput.Kv("子弧内的子项数", view.ChildItemCount);
            ProbeOutput.Kv("子弧展开进度", view.ChildArcProgress.ToString("0.###"));
            ProbeOutput.Kv("最大溢出", $"{view.MaxLabelOverflow:0.#} px");
            ProbeOutput.Line();

            var missing = count - view.RenderedIconCount;
            if (missing > 0)
            {
                ProbeOutput.Line($"  注意：{missing} 项取不到图标，已退化为首字占位图。");
                ProbeOutput.Line("  占位是刻意设计 —— 单个图标失败不该让整项看起来像坏了。");
            }

            ProbeOutput.Line();
            ProbeOutput.Line("  下一步：tools/analyze-render.ps1 做数值验证。");
            ProbeOutput.Line($"  扇区应以 i*{360.0 / count:0.#}° 为中心（0° = 正上），缝隙在相邻中心的中点。");
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("渲染失败: " + ex);
        }

        ProbeOutput.Flush();
    }

    /// <summary>
    /// 标签实际可用的宽度。
    /// 直接调 <see cref="Core.Design.RadiusTokens.LabelMaxHalfWidth"/>，
    /// 而不是在这里重算一遍 —— 重算的话报告会和实现悄悄分叉
    /// （第一版就写成了只算扇区弦长，没有计入"不压到图标"这条约束，报出来的宽度偏大）。
    /// </summary>
    private static double AvailableWidthFor(Core.Design.DesignTokens tokens, int count) =>
        Core.Design.RadiusTokens.LabelMaxHalfWidth(tokens.Radius, count) * 2;
}
