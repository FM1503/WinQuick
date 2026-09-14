using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Interop;

/// <summary>
/// 原生 Win32 全屏遮罩窗口 —— 完全不经过 WPF。
///
/// 为什么不用 WPF（实测驱动）：WPF 版遮罩窗口哪怕只是隐藏着也要多占 61MB 工作集，
/// 远超其原始像素（7.7MB），说明 WPF 为纯色窗口也分配了完整渲染目标。
/// 原生窗口实测只占 0.1MB。
///
/// 为什么必须注册自己的窗口类，而不是复用系统预定义的 STATIC 类：
///   1. STATIC 的背景画刷是系统的「按钮表面色」（浅灰）。LWA_ALPHA 只给它加透明度，
///      得到的是一层灰纱而不是黑纱 —— 用户看到的正是这个。
///   2. **用预定义类时 CreateWindowEx 不会使用我们提供的窗口过程**，
///      于是「点空白处关闭」与 WM_MOUSEACTIVATE 处理全部失效。
/// 这两点都是靠用户反馈 + 事后核对才发现的，症状分别是「遮罩不是黑的」和「点空白处没反应」。
///
/// 交互语义：
/// - WS_EX_NOACTIVATE + WM_MOUSEACTIVATE 返回 MA_NOACTIVATE：点击不抢焦点，但收得到点击
/// - WS_EX_TOOLWINDOW：不出现在 Alt+Tab（唯一能可靠做到这点的样式）
/// - 鼠标按下即触发 DismissRequested（「点空白处关闭」）
/// </summary>
public sealed class RawDimWindow : IDisposable
{
    private const string WindowClassName = "WinQuick.DimMask";

    /// <summary>遮罩透明度（0-255）。40% 黑是压暗但不遮住内容的常见取值。</summary>
    public const byte DefaultAlpha = 102;

    // 窗口过程是类级别的，一个类只有一个，所以用 HWND → 实例的映射把消息转发回对应对象。
    // 用 IntPtr 作键：HWND 的 Value 是 void*，直接碰它需要在每个调用点标 unsafe，
    // 而 HWND 有到 IntPtr 的隐式转换，用它更干净。
    private static readonly Dictionary<IntPtr, RawDimWindow> Instances = [];
    private static readonly object ClassGate = new();

    private static bool _classRegistered;
    // 必须静态持有：窗口过程被 GC 掉的话，所有遮罩窗口都会失去消息处理。
    private static WNDPROC? _sharedProc;

    private readonly HWND _hwnd;
    private bool _disposed;

    /// <summary>鼠标在遮罩上按下时触发 —— 语义是「点击轮盘以外的区域」。</summary>
    public event Action? DismissRequested;

    public unsafe RawDimWindow(byte alpha = DefaultAlpha)
    {
        EnsureClassRegistered();

        var hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED
                | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                | WINDOW_EX_STYLE.WS_EX_TOPMOST,
            WindowClassName,
            "WinQuick.DimMask",
            WINDOW_STYLE.WS_POPUP,
            0, 0, 1, 1,
            default,
            default,
            default,
            null);

        _hwnd = hwnd;

        if (hwnd == default)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"创建原生遮罩窗口失败，GetLastError={err}");
        }

        lock (Instances)
            Instances[(IntPtr)hwnd] = this;

        // 统一 alpha：不需要逐像素合成，所以代价与窗口面积无关，全屏也几乎零成本。
        SetAlpha(alpha);
    }

    /// <summary>
    /// 就地更新遮罩透明度。
    ///
    /// 这是改透明度的正确做法 —— **不需要销毁重建窗口**。
    /// 之前拖动"遮罩深浅"滑块时走的是"销毁整屏遮罩窗口 + 重建"，
    /// 那会在每次滑动时让整个屏幕闪一下（用户报的"一直闪烁"就有这一份）。
    /// 改 alpha 只是一个分层窗口属性，系统重新合成一次即可，观感上是连续变化的。
    /// </summary>
    public void SetAlpha(byte alpha)
    {
        if (!IsValid) return;
        PInvoke.SetLayeredWindowAttributes(
            _hwnd, default, alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    internal nint HandleValue => _hwnd;

    public bool IsValid => _hwnd != default && !_disposed;

    private static unsafe void EnsureClassRegistered()
    {
        lock (ClassGate)
        {
            if (_classRegistered) return;

            _sharedProc = StaticProc;

            fixed (char* className = WindowClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    // CS_HREDRAW|CS_VREDRAW：尺寸变化时整窗重绘。遮罩会随显示器变更
                    // 重新铺满，不重绘会留下旧的填充区域。
                    style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
                    lpfnWndProc = _sharedProc,
                    // 关键：纯黑画刷。系统用它在 WM_ERASEBKGND 时填充背景，
                    // 于是 LWA_ALPHA 得到的就是半透明黑，而不是预定义类的灰色。
                    hbrBackground = (HBRUSH)PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.BLACK_BRUSH).Value,
                    lpszClassName = className,
                    hCursor = PInvoke.LoadCursor(default, PInvoke.IDC_ARROW),
                };

                var atom = PInvoke.RegisterClassEx(wc);

                // 已注册会返回 0 并置 ERROR_CLASS_ALREADY_EXISTS(1410)，这不算失败。
                _classRegistered = atom != 0 || Marshal.GetLastWin32Error() == 1410;
            }
        }
    }

    private static LRESULT StaticProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        RawDimWindow? self;
        lock (Instances)
            Instances.TryGetValue((IntPtr)hwnd, out self);

        return self is not null
            ? self.Proc(hwnd, msg, wParam, lParam)
            : PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private LRESULT Proc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            // 点击不激活，但点击事件照常送达 —— 这正是「点空白处关闭」需要的组合。
            // 返回 MA_NOACTIVATE 而非 MA_ACTIVATE，是为了不顶掉用户当前的前台窗口。
            case PInvoke.WM_MOUSEACTIVATE:
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);

            case PInvoke.WM_LBUTTONDOWN:
            case PInvoke.WM_RBUTTONDOWN:
            case PInvoke.WM_MBUTTONDOWN:
                DismissRequested?.Invoke();
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void SetPhysicalBounds(int x, int y, int width, int height)
    {
        if (!IsValid) return;
        PInvoke.SetWindowPos(_hwnd, default, x, y, width, height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
    }

    public void Show() => ShowOrHide(true);
    public void Hide() => ShowOrHide(false);

    private void ShowOrHide(bool visible)
    {
        if (!IsValid) return;
        // SW_SHOWNOACTIVATE：显示但不激活，绝不能把用户正在用的窗口顶掉。
        PInvoke.ShowWindow(_hwnd, visible
            ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE
            : SHOW_WINDOW_CMD.SW_HIDE);
    }

    /// <summary>
    /// 把窗口重新插回 topmost band 最前。
    /// 走 <see cref="ZOrderManager"/> —— 之前这里手写的调用带了 SWP_NOZORDER，是空操作。
    /// </summary>
    public void ReassertTopmost()
    {
        if (!IsValid) return;
        ZOrderManager.BringToTopmostFront(_hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != default)
        {
            lock (Instances)
                Instances.Remove((IntPtr)_hwnd);

            PInvoke.DestroyWindow(_hwnd);
        }
    }
}
