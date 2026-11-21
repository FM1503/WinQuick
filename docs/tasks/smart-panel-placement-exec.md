# 执行书：smart-panel-placement

## Q1 新建 src/WinQuick.App/Diagnostics/PanelPlacement.cs（全文）

```csharp
namespace WinQuick.App.Diagnostics;

/// <summary>
/// 面板摆位（spec：smart-panel-placement-childarc-asyncload §2.1）。
///
/// 纯函数：不碰 WPF、不碰窗口，只做几何 —— 「摆哪儿」这类屏坐标/缩放/钳制三重换算
/// 最容易写错，只有能被单测直接断言的纯函数才锁得住。
/// 策略：零重叠优先 —— 候选锚位中与轮盘内切圆重叠为零者取离光标最近；
/// 窄屏全重叠时取重叠最小；并列取稳定序（候选序即左上优先），同一输入永远同一结果。
/// </summary>
public static class PanelPlacement
{
    /// <summary>全部物理像素。</summary>
    public readonly record struct Input(
        int MonitorX, int MonitorY, int MonitorW, int MonitorH,
        int RingX, int RingY, int RingSize,
        int PanelW, int PanelH, int Margin,
        int CursorX, int CursorY);

    public readonly record struct Result(int Left, int Top, double OverlapArea);

    public static Result Choose(Input t)
    {
        var m = t.Margin;
        int w = t.PanelW, h = t.PanelH;
        int mx = t.MonitorX, my = t.MonitorY;
        int mw = t.MonitorW, mh = t.MonitorH;

        // 候选锚位：四角、上下边中点、左右中点、左右对侧（垂直跟随光标 —— 用户偏好
        // 「在光标附近但不挡」，对侧贴边 + 跟随垂直位置是最常用的落点）。
        var midX = mx + (mw - w) / 2;
        var midY = my + (mh - h) / 2;
        var followTop = Clamp(t.CursorY - h / 2, my + m, my + mh - h - m);
        var candidates = new (int Left, int Top)[]
        {
            (mx + m, my + m),
            (mx + mw - w - m, my + m),
            (mx + m, my + mh - h - m),
            (mx + mw - w - m, my + mh - h - m),
            (midX, my + m),
            (midX, my + mh - h - m),
            (mx + m, midY),
            (mx + mw - w - m, midY),
            (mx + m, followTop),
            (mx + mw - w - m, followTop),
        };

        var cx = t.RingX + t.RingSize / 2.0;
        var cy = t.RingY + t.RingSize / 2.0;
        var r = t.RingSize / 2.0;

        Result best = default;
        var bestKey = double.MaxValue;
        for (var i = 0; i < candidates.Length; i++)
        {
            // 小屏上面板可能比「屏减边距」还大：先钳回屏内再评估，否则底部按钮点不到。
            var l = Clamp(candidates[i].Left, mx, Math.Max(mx, mx + mw - w));
            var tp = Clamp(candidates[i].Top, my, Math.Max(my, my + mh - h));
            var overlap = CircleRectOverlapArea(cx, cy, r, l, tp, w, h);
            var pcx = l + w / 2.0;
            var pcy = tp + h / 2.0;
            var dx = pcx - t.CursorX;
            var dy = pcy - t.CursorY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            // 字典序：重叠面积优先压倒距离（零重叠者优先），距离次之；
            // +i*1e-6 让完全并列时候选序小者（左上优先）稳定胜出。
            var key = overlap * 1e6 + dist + i * 1e-6;
            if (key < bestKey)
            {
                bestKey = key;
                best = new Result(l, tp, overlap);
            }
        }
        return best;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>
    /// 圆与轴对齐矩形的重叠面积：32×32 网格中心采样。
    /// 对「候选之间比大小」这个用途，采样精度足够（误差远小于候选间的面积差），
    /// 且避免圆-矩形解析交的分支地狱 —— 那种代码没人能一眼看对。
    /// </summary>
    private static double CircleRectOverlapArea(double cx, double cy, double r, int l, int t, int w, int h)
    {
        const int n = 32;
        var inside = 0;
        for (var iy = 0; iy < n; iy++)
        {
            var y = t + (iy + 0.5) * h / n;
            var dy = y - cy;
            for (var ix = 0; ix < n; ix++)
            {
                var x = l + (ix + 0.5) * w / n;
                var dx = x - cx;
                if (dx * dx + dy * dy <= r * r) inside++;
            }
        }
        return (double)inside / (n * n) * w * h;
    }
}
```

## Q2 新建 tests/WinQuick.Core.Tests/PanelPlacementTests.cs（全文）

```csharp
using WinQuick.App.Diagnostics;

namespace WinQuick.Core.Tests;

/// <summary>
/// 摆位算法的几何契约（spec §2.1）：零重叠优先、距离决胜、窄屏最小重叠、
/// 钳制不溢屏、并列稳定。断言用「性质」而不是精确坐标 —— 坐标是实现的细节，
/// 性质（不挡/不出屏/可复现）才是用户能感知的契约。
/// </summary>
public class PanelPlacementTests
{
    private static PanelPlacement.Result Choose(
        int mw, int mh, int ringX, int ringY, int ringSize,
        int panelW, int panelH, int cursorX, int cursorY, int margin = 24) =>
        PanelPlacement.Choose(new PanelPlacement.Input(
            0, 0, mw, mh, ringX, ringY, ringSize, panelW, panelH, margin, cursorX, cursorY));

    [Fact]
    public void 大屏光标左半_零重叠且面板在轮盘右侧()
    {
        // 轮盘在左半（直径 500），光标即轮盘中心。
        var r = Choose(2560, 1440, 200, 470, 500, 404, 800, 450, 720);
        Assert.Equal(0, r.OverlapArea, 1);
        Assert.True(r.Left > 200 + 500); // 面板整体在轮盘右缘之外
    }

    [Fact]
    public void 轮盘居中_零重叠取最近候选()
    {
        var r = Choose(1920, 1080, 710, 290, 500, 404, 700, 960, 540); // 光标=屏中心=轮盘中心
        Assert.Equal(0, r.OverlapArea, 1);
        // 左右中点与四角均零重叠，但中点候选（垂直居中）比四角近约 18px；
        // 左右中点对称并列 → 稳定序取左中点 (24, 190)。上/下中候选与轮盘大面积重叠被排除。
        Assert.Equal(24, r.Left);
        Assert.Equal(190, r.Top);
    }

    [Fact]
    public void 光标近右下_右下角胜出()
    {
        var r = Choose(1920, 1080, 100, 100, 400, 404, 700, 1800, 1000);
        Assert.Equal(0, r.OverlapArea, 1);
        Assert.Equal(1920 - 404 - 24, r.Left);
        Assert.Equal(1080 - 700 - 24, r.Top);
    }

    [Fact]
    public void 窄屏必重叠_返回正的最小重叠()
    {
        // 屏 900×600 放不下「轮盘 500 + 面板 404 宽」的不重叠布局。
        var r = Choose(900, 600, 200, 50, 500, 404, 700, 450, 300);
        Assert.True(r.OverlapArea > 0);
        Assert.InRange(r.Left, 0, 900 - 404);
        Assert.InRange(r.Top, 0, Math.Max(0, 600 - 700) ); // 面板高于屏时钳到屏内
        Assert.True(r.Top >= 0);
    }

    [Fact]
    public void 面板高于屏_钳制在屏内不溢出()
    {
        var r = Choose(1280, 600, 400, 50, 400, 404, 900, 640, 300);
        Assert.True(r.Top >= 0);
        Assert.True(r.Top + 900 <= 600 || r.Top == 0); // 溢出时贴顶（MaxHeight 兜底的场景）
    }

    [Fact]
    public void 轮盘贴左缘_面板落右半()
    {
        var r = Choose(1920, 1080, 0, 290, 500, 404, 700, 250, 540);
        Assert.Equal(0, r.OverlapArea, 1);
        Assert.True(r.Left >= 500 - 1); // 与轮盘右缘只差采样容差
    }

    [Fact]
    public void 同输入两次调用_结果一致()
    {
        var a = Choose(1920, 1080, 710, 290, 500, 404, 700, 300, 200);
        var b = Choose(1920, 1080, 710, 290, 500, 404, 700, 300, 200);
        Assert.Equal(a, b);
    }

    [Fact]
    public void 轮盘尺寸为零的退化输入_不抛异常且不出屏()
    {
        var r = Choose(1920, 1080, 960, 540, 0, 404, 700, 960, 540);
        Assert.InRange(r.Left, 0, 1920 - 404);
        Assert.InRange(r.Top, 0, 1080 - 700);
        Assert.Equal(0, r.OverlapArea, 1);
    }

    [Fact]
    public void 对侧候选垂直跟随光标()
    {
        // 光标在左下：右侧对侧候选的 Top 应显著偏下（跟随），而不是几何居中。
        var low = Choose(1920, 1440, 200, 1000, 400, 404, 700, 400, 1300);
        Assert.True(low.Top > 1440 / 2 - 700 / 2, "对侧候选应跟随光标下移");
    }
}
```

## Q3 OverlayController.cs PositionPanel 接线

先 Read 定向 1178-1210 行核对 old（以实际为准），将「cursorOnLeftHalf 起 … top 钳制止」的计算段替换：

old:
```
        var cursorOnLeftHalf = !MonitorLayout.TryGetCursorPosition(out var cx, out _)
                               || cx < monitor.X + monitor.Width / 2;

        var left = cursorOnLeftHalf
            ? monitor.Right - panelWidth - (int)Math.Round(margin * scale0)
            : monitor.X + (int)Math.Round(margin * scale0);

        var top = monitor.Y + (monitor.Height - panelHeight) / 2;

        // 钳制在屏内。竖直方向尤其重要：面板是纵向表单，底部是最常用的按钮，
        // 溢出屏幕就等于点不到 —— 用户已经因为"按钮点不到"报过一次问题了。
        left = Math.Clamp(left, monitor.X, Math.Max(monitor.X, monitor.Right - panelWidth));
        top = Math.Clamp(top, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - panelHeight));
```

new:
```
        // 摆位策略：零重叠优先（spec smart-panel-placement-childarc-asyncload §2.1）。
        // 轮盘矩形取常驻窗口的物理边界；拿不到时退化为点圆（全候选零重叠，按距离选）。
        MonitorLayout.TryGetCursorPosition(out var cx, out var cy);
        if (cx == 0 && cy == 0)
        {
            cx = monitor.X + monitor.Width / 2;
            cy = monitor.Y + monitor.Height / 2;
        }
        var bounds = _radial.CurrentPhysicalBounds;
        var choice = Diagnostics.PanelPlacement.Choose(new Diagnostics.PanelPlacement.Input(
            monitor.X, monitor.Y, monitor.Width, monitor.Height,
            bounds?.X ?? cx, bounds?.Y ?? cy, bounds?.Width ?? 0,
            panelWidth, panelHeight, (int)Math.Round(margin * scale0),
            cx, cy));
        var left = choice.Left;
        var top = choice.Top;
```

## 验证

1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` → 0 error；
2. `dotnet test tests/WinQuick.Core.Tests --filter PanelPlacement --nologo` → 9 用例全绿；
3. `dotnet test tests/WinQuick.Core.Tests --nologo` → 全量基线不回退（312+9）。

old 不匹配即停报。
