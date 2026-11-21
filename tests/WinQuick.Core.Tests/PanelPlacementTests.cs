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
    public void 对侧跟随候选在轮盘居中光标时胜出()
    {
        // 轮盘跟光标走（真实耦合）：光标 (850,450) 居屏中上，轮盘以它为中心钳屏后 (600,200,500)。
        // 左跟随候选 (24,100) 比所有固定候选都近 —— 锁定「跟随」行为（复审模拟的分叉例）。
        var r = Choose(1920, 1440, 600, 200, 500, 404, 700, 850, 450);
        Assert.Equal(24, r.Left);
        Assert.Equal(100, r.Top);
    }
}
