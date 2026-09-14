using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Hosting;

/// <summary>
/// 覆盖层的编排：遮罩层 + 轮盘层的显示/隐藏、焦点保存还原、置顶维持、显示器变更重建。
///
/// 窗口在启动时就全部创建好并保持隐藏（<see cref="Prewarm"/>）。这是刻意的：
/// Kando 在 1.8.0 引入懒初始化后用户抱怨"首次打开明显变慢"，宁可退回旧版本。
/// 预热让热键回调只需要 Show + 启动动画，这是达成「按下到可见 &lt;100ms」的前提。
/// </summary>
public sealed class OverlayController : IDisposable
{
    /// <summary>
    /// 置顶重申间隔。WS_EX_TOPMOST 只保证压住非 topmost 窗口，
    /// 不高于其他 topmost（任务管理器、其他常驻置顶工具都在同一个 band 里）。
    /// 没有文档化的更高层级，所以只能周期性重申 —— 这是行业惯例做法。
    /// </summary>
    private static readonly TimeSpan TopmostReassertInterval = TimeSpan.FromMilliseconds(800);

    private readonly List<RawDimWindow> _dims = [];
    private RadialWindow _radial;   // 菜单热重载时会整体替换，所以不是 readonly
    private readonly ForegroundFocus _focus = new();
    private readonly DispatcherTimer _topmostTimer;

    /// <summary>退场动画播完后才真正隐藏窗口。动画不影响动作执行，只影响"什么时候看不见"。</summary>
    private readonly DispatcherTimer _exitTimer;
    private HwndSource? _radialSource;

    private bool _disposed;

    public OverlayController(MenuSpec menu)
    {
        _radial = CreateRadialWindow(menu);

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TopmostReassertInterval,
        };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();

        _exitTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _exitTimer.Tick += (_, _) =>
        {
            _exitTimer.Stop();
            FinishHide();
        };
    }

    private RadialWindow CreateRadialWindow(MenuSpec menu)
    {
        var window = new RadialWindow(menu);
        window.DismissRequested += (_, _) => Hide();
        window.ItemActivated += (_, selection) => ItemActivated?.Invoke(this, selection);
        return window;
    }

    /// <summary>
    /// 换用另一份菜单。
    ///
    /// 必须重建轮盘窗口，因为视觉树是用菜单在构造时建立的 —— 与其让视图支持
    /// 就地重建（要小心处理子弧、悬停状态、动画器等一堆状态），
    /// 不如整体替换来得干净。菜单变更本来就是低频的用户主动操作。
    /// 替换后立刻预热，所以随后的首次唤出仍然不慢。
    /// </summary>
    public void RebuildForMenu(MenuSpec menu)
    {
        if (_disposed) return;

        var wasVisible = IsVisible;
        ForceHideAndRebuild();

        _radialSource?.RemoveHook(OnRadialMessage);
        var previous = _radial;
        _radial = CreateRadialWindow(menu);
        _radialSource = null;

        try { previous.Close(); } catch { /* 已关闭则忽略 */ }

        Prewarm();

        if (wasVisible) Show();
    }

    /// <summary>遮罩不透明度的用户覆盖值（0–1）。null 表示沿用设计基线。</summary>
    public double? DimOpacityOverride { get; set; }

    public bool IsVisible { get; private set; }

    /// <summary>诊断用：遮罩窗口句柄。供 --overlay-probe 核对窗口真实状态，而不是相信自己的布尔量。</summary>
    internal IReadOnlyList<nint> DimHandles => _dims.Select(d => d.HandleValue).ToList();

    /// <summary>诊断用：轮盘窗口句柄。</summary>
    internal nint RadialHandle => _radial.HandleValue;

    /// <summary>诊断用：子弧是否展开。</summary>
    internal bool IsChildArcExpanded => _radial.IsChildArcVisible;

    /// <summary>诊断用：子弧展开进度。</summary>
    internal double ChildArcProgress => _radial.ChildArcProgress;

    /// <summary>诊断用：渲染出来的子项数量。</summary>
    internal int ChildItemCount => _radial.ChildItemCount;

    /// <summary>诊断用：视觉树上的直接子元素数量。</summary>
    internal int VisualChildCount => _radial.VisualChildCount;

    /// <summary>诊断用：跳过悬停延迟直接展开子弧。</summary>
    internal void ExpandChildArcForProbe(int parentIndex) => _radial.ExpandChildArc(parentIndex);

    internal void CollapseChildArcForProbe() => _radial.CollapseChildArc();

    /// <summary>
    /// 某个叶子项被点击。<c>ChildIndex</c> 为 -1 表示第一层的项，否则是子弧里的子项。
    /// </summary>
    public event EventHandler<(int ParentIndex, int ChildIndex)>? ItemActivated;

    /// <summary>
    /// 预先创建所有窗口并隐藏。必须在启动时调用一次，把渲染栈、字体缓存、
    /// D3D 表面都热起来 —— 否则第一次唤出会明显卡顿。
    /// </summary>
    public void Prewarm()
    {
        RebuildDimWindows();

        // 让轮盘窗口真正渲染一次再隐藏：只创建不显示的话，字体与渲染目标都还没分配，
        // 第一次唤出仍然会卡。
        //
        // ShowActivated 必须先关掉 —— 否则这个预热用的 Show 会在开机时把焦点从
        // 用户正在用的窗口抢走。位置也放到屏幕外，避免开机闪一下。
        _radial.ShowActivated = false;
        _radial.Left = -32000;
        _radial.Top = -32000;
        _radial.Show();
        _radial.Hide();
        _radial.ShowActivated = true;

        // 窗口句柄此时才存在，所以消息钩子只能在这里挂。
        // 显示器拓扑变化后遮罩窗口的尺寸全都会失效，必须重建窗口池。
        _radialSource = (HwndSource?)PresentationSource.FromVisual(_radial);
        _radialSource?.AddHook(OnRadialMessage);
    }

    /// <summary>
    /// 显示覆盖层。
    /// **必须同步完成** —— 前台权限的豁免只在同一次输入事件的调用栈内有效，
    /// 一旦 await、切线程或 PostMessage 之后再设置前台窗口，权限就丢了。
    /// </summary>
    public void Show()
    {
        if (_disposed || IsVisible) return;

        // 显示器数量变了就重建遮罩窗口池，否则会有屏没被遮住。
        if (_dims.Count != MonitorLayout.All().Count)
            RebuildDimWindows();

        _focus.Save();

        foreach (var dim in _dims)
        {
            dim.ReassertTopmost();
            dim.Show();
        }

        // 先摆位置再让 WPF 显示 —— 顺序反了会先在旧位置闪一帧。
        _radial.PlaceAtCursor();
        _radial.Show();
        _radial.ReassertTopmost();
        _radial.PlayEntrance();
        _radial.StartInteraction();

        // 拿键盘焦点，否则 Esc 收不到。这里同步调用是刻意的（见方法注释）。
        ForegroundFocus.AttachTo(_radial);

        IsVisible = true;
        _topmostTimer.Start();
    }

    /// <summary>
    /// 隐藏覆盖层并把前台焦点还给用户原来的窗口。
    ///
    /// 遮罩层立刻消失（它是用户的视觉反馈），轮盘窗口等退场动画播完再隐藏。
    /// 焦点**立刻**归还 —— 用户按下扇区之后应马上回到原来的窗口，
    /// 不能等动画放完才还（Kando #650 的教训）。
    /// </summary>
    public void Hide()
    {
        if (!IsVisible) return;
        IsVisible = false;

        _topmostTimer.Stop();
        // 指针轮询必须停 —— 它每 25ms 唤醒一次 UI 线程，窗口不可见时纯属浪费。
        _radial.StopInteraction();
        foreach (var dim in _dims) dim.Hide();

        // 退场期间不再接受输入，否则用户在收缩过程中还能点到扇区。
        _radial.IsHitTestVisible = false;

        var exitMs = TokenStore.Current.Motion.ExitDurationMs;
        if (AnimationsEnabled() && exitMs > 0)
        {
            _radial.PlayExit();
            _exitTimer.Interval = TimeSpan.FromMilliseconds(exitMs + 20);
            _exitTimer.Start();
        }
        else
        {
            FinishHide();
        }

        // 焦点立刻归还，不等动画。
        _focus.Restore();
    }

    /// <summary>真正把轮盘窗口隐藏并重置动画状态。</summary>
    private void FinishHide()
    {
        _radial.Hide();
        _radial.ResetAnimationState();
        _radial.ResetInteraction();
        _radial.IsHitTestVisible = true;
    }

    private static bool AnimationsEnabled()
    {
        try { return SystemParameters.ClientAreaAnimation; }
        catch { return true; }
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    /// <summary>显示器变更、会话解锁等场景下的强制收尾 —— 宁可多关一次，也不要留下残影。</summary>
    public void ForceHideAndRebuild()
    {
        // 这条路径**不等动画**，直接立刻隐藏：异常场景下确定性比观感重要。
        _exitTimer.Stop();

        if (IsVisible)
        {
            IsVisible = false;
            _topmostTimer.Stop();
            _focus.Restore();
        }

        FinishHide();
        foreach (var dim in _dims) dim.Hide();
        RebuildDimWindows();
    }

    private void RebuildDimWindows()
    {
        foreach (var dim in _dims)
        {
            // 事件解绑后再销毁，避免已销毁的窗口回调打到活着的控制器上。
            dim.DismissRequested -= OnDimDismiss;
            dim.Dispose();
        }
        _dims.Clear();

        // 每屏一个遮罩窗口。一个 HWND 只能有一个 DPI，所以不能合并成一个横跨的窗口。
        // 遮罩用统一 alpha（40% 黑），原生窗口实测几乎零内存开销。
        // 透明度可以由用户配置覆盖；默认用设计基线里的值。
        var alpha = DimOpacityOverride is { } opacity
            ? (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255)
            : RawDimWindow.DefaultAlpha;

        foreach (var monitor in MonitorLayout.All())
        {
            var dim = new RawDimWindow(alpha);
            dim.SetPhysicalBounds(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            dim.DismissRequested += OnDimDismiss;
            _dims.Add(dim);
        }
    }

    private void OnDimDismiss()
    {
        // 点在遮罩上 = 点在轮盘之外的空白处。
        Hide();
    }

    private void ReassertTopmost()
    {
        foreach (var dim in _dims) dim.ReassertTopmost();
        _radial.ReassertTopmost();
    }

    private IntPtr OnRadialMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_* 在 CsWin32 里是 uint，而消息参数是 int，所以这里转成 uint 再比对。
        switch ((uint)msg)
        {
            case PInvoke.WM_DISPLAYCHANGE:
                // 退出全屏游戏、插拔显示器、改分辨率都会走到这里。
                Hide();
                RebuildDimWindows();
                break;

            case PInvoke.WM_KILLFOCUS:
                // 轮盘失去键盘焦点（用户点了别处）。窗口的 Deactivated 事件覆盖不到
                // 所有路径，这里作为兜底。
                if (IsVisible) Hide();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _topmostTimer.Stop();
        _exitTimer.Stop();
        _radialSource?.RemoveHook(OnRadialMessage);

        foreach (var dim in _dims)
        {
            dim.DismissRequested -= OnDimDismiss;
            dim.Dispose();
        }
        _dims.Clear();

        _radial.Close();
    }
}
