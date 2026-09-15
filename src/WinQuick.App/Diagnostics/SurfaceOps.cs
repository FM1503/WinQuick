using System.Windows;

namespace WinQuick.App.Diagnostics;

/// <summary>哪一块分层窗口。</summary>
internal enum SurfaceOwner
{
    Wheel,
    Panel,
    Bar,
}

/// <summary>
/// 分层窗口的"结构性操作"计数器 —— 凡是会让合成器重建、或重新取走整块表面的操作，
/// 都记在这里。
///
/// 为什么要数：分层窗口（<c>AllowsTransparency</c> + 逐像素 alpha）与普通窗口不同，
/// 它的整块表面由进程通过 <c>UpdateLayeredWindow</c> 交给合成器。**改尺寸、换内容对象、
/// 发 SetWindowPos、显隐**都会让这块表面重建一次，观感上就是"闪一下"。
///
/// 而"闪"本身发生在两帧之间，进程内部看不见。能看见的只有"哪些操作发生了" ——
/// 所以把它们数出来：让一段脚本化的"典型刷新"跑一遍，看哪个计数在涨，
/// 就能把"刷新时频繁闪"落到具体机制上，而不是接着猜。
///
/// 判据很简单：**纯内容刷新（换文字、换配色、换菜单项、悬停、拖拽）一个都不该涨。**
/// 涨了就是机制问题，得改实现而不是改观感。
///
/// 用法见 <c>--flicker-probe</c>。
/// </summary>
internal static class SurfaceOps
{
    private static int _resizeWheel, _resizePanel, _resizeBar;
    private static int _visibleWheel, _visiblePanel, _visibleBar;
    private static int _menuRebuild, _contentSwap, _dimRebuild, _dimAlpha, _setPos;

    /// <summary>最后一次 SetWindowPos 是谁发的。用来分辨"是计时器在重申"还是"有人在摆位"。</summary>
    private static volatile string _lastSetPosSite = "（无）";

    /// <summary>最后一次改尺寸是从多少到多少 —— "涨了 1 次"不够用，得知道变的是什么。</summary>
    private static volatile string _lastResize = "（无）";

    /// <summary>
    /// 挂上尺寸与可见性的事件。**必须在窗口构造时就调用** —— 晚一步就会漏掉前面几次。
    /// </summary>
    public static void Watch(Window window, SurfaceOwner owner)
    {
        window.SizeChanged += (_, e) =>
        {
            _lastResize = $"{owner}：{e.PreviousSize.Width:0.#}×{e.PreviousSize.Height:0.#}"
                        + $" → {e.NewSize.Width:0.#}×{e.NewSize.Height:0.#} DIP";
            Interlocked.Increment(ref Counter(owner, visible: false));
        };
        window.IsVisibleChanged += (_, _) => Interlocked.Increment(ref Counter(owner, visible: true));
    }

    /// <summary>换了一次视觉树。<paramref name="swapped"/> 为真表示换掉的是**另一个对象** —— 那会走一次窗口级布局。</summary>
    public static void MenuRebuild(bool swapped)
    {
        Interlocked.Increment(ref _menuRebuild);
        if (swapped) Interlocked.Increment(ref _contentSwap);
    }

    /// <summary>重建了整屏遮罩窗口（新建一块全屏表面）。</summary>
    public static void DimRebuild() => Interlocked.Increment(ref _dimRebuild);

    /// <summary>只改了遮罩的 alpha（就地更新，不重建表面）。</summary>
    public static void DimAlpha() => Interlocked.Increment(ref _dimAlpha);

    /// <summary>发了一次 SetWindowPos。<paramref name="site"/> 是调用点。</summary>
    public static void SetWindowPos(string site)
    {
        _lastSetPosSite = site;
        Interlocked.Increment(ref _setPos);
    }

    public static SurfaceSnapshot Now() => new(
        Volatile.Read(ref _resizeWheel), Volatile.Read(ref _resizePanel), Volatile.Read(ref _resizeBar),
        Volatile.Read(ref _visibleWheel), Volatile.Read(ref _visiblePanel), Volatile.Read(ref _visibleBar),
        Volatile.Read(ref _menuRebuild), Volatile.Read(ref _contentSwap),
        Volatile.Read(ref _dimRebuild), Volatile.Read(ref _dimAlpha),
        Volatile.Read(ref _setPos), _lastSetPosSite, _lastResize);

    private static ref int Counter(SurfaceOwner owner, bool visible)
    {
        if (owner == SurfaceOwner.Wheel) return ref (visible ? ref _visibleWheel : ref _resizeWheel);
        if (owner == SurfaceOwner.Panel) return ref (visible ? ref _visiblePanel : ref _resizePanel);
        return ref (visible ? ref _visibleBar : ref _resizeBar);
    }
}

/// <summary>某一刻的全部计数。差值才是要看的东西 —— 绝对值里混着初始化那几次。</summary>
internal readonly record struct SurfaceSnapshot(
    int WheelResize, int PanelResize, int BarResize,
    int WheelVisible, int PanelVisible, int BarVisible,
    int MenuRebuild, int ContentSwap,
    int DimRebuild, int DimAlpha,
    int SetPos, string LastSetPosSite, string LastResize)
{
    /// <summary>重建整块表面的操作数（改尺寸 + 换内容对象 + 显隐 + 重建遮罩）。</summary>
    public int SurfaceRebuilds =>
        WheelResize + PanelResize + BarResize + WheelVisible + PanelVisible + BarVisible +
        ContentSwap + DimRebuild;

    /// <summary>只列出真的涨了的项。一项都没涨时是「无」—— 那正是纯内容刷新应有的样子。</summary>
    public string DeltaFrom(SurfaceSnapshot b)
    {
        var parts = new List<string>();

        void Add(string name, int now, int before)
        {
            if (now != before) parts.Add($"{name}×{now - before}");
        }

        Add("轮盘改尺寸", WheelResize, b.WheelResize);
        Add("面板改尺寸", PanelResize, b.PanelResize);
        Add("操作条改尺寸", BarResize, b.BarResize);
        Add("轮盘显隐", WheelVisible, b.WheelVisible);
        Add("面板显隐", PanelVisible, b.PanelVisible);
        Add("操作条显隐", BarVisible, b.BarVisible);
        Add("换视觉树", MenuRebuild, b.MenuRebuild);
        Add("换内容对象", ContentSwap, b.ContentSwap);
        Add("重建遮罩", DimRebuild, b.DimRebuild);
        Add("只改遮罩alpha", DimAlpha, b.DimAlpha);
        Add("SetWindowPos", SetPos, b.SetPos);

        if (parts.Count == 0) return "无";

        // 后缀只在**这一类**真的发生时带 —— 否则"换了一次视觉树"后面会跟着一句
        // 上一次定位留下的地点，读起来像是它在发 SetWindowPos。
        var text = string.Join("，", parts);
        if (SetPos != b.SetPos) text += $"（最后来自：{LastSetPosSite}）";
        if (WheelResize != b.WheelResize || PanelResize != b.PanelResize || BarResize != b.BarResize)
            text += $"（最后一次改尺寸：{LastResize}）";
        return text;
    }
}
