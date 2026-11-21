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
            // 对侧贴边 + 垂直跟随光标：轮盘跟着光标走，光标在屏中上部时轮盘也在中上，
            // 此时对侧跟随位比任何固定候选都离光标近（复审模拟：约 20-30% 场景由它胜出）。
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
