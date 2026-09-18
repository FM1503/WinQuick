using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Interop;

/// <summary>
/// 按住某个元素拖动整个窗口（物理像素级）。
///
/// ## 为什么不用 <c>Window.DragMove()</c>
///
/// 最初用的是它，实测**拖不动**：它要求窗口处于激活状态（内部会检查 <c>IsActive</c>），
/// 不满足就抛异常。当时把异常静默吞了，于是表现成"按下去毫无反应"，连原因都看不到。
/// 吞异常比抛异常更坏。
///
/// ## 为什么用物理像素
///
/// 记录的是 <c>GetCursorPos</c> / <c>GetWindowRect</c> 的原始像素，位移直接加到窗口左上角，
/// 全程绕开 DIP 换算 —— 在 175% 这类非整数缩放下按 DIP 算会逐帧累积误差，越拖越偏。
///
/// ## 为什么监听整个窗口而不是只监听把手
///
/// 窗口会跟着指针走，指针始终落在窗口内，所以不捕获鼠标也收得到后续事件 ——
/// 而"捕获鼠标"恰恰需要我们是前台窗口，那是一条本项目已经踩过的坑。
///
/// 设置面板与皮肤面板共用这一份：两处各写一遍的话，迟早只有一处被修好。
/// </summary>
internal static class WindowDrag
{
    /// <summary>每个窗口一份拖动状态。用弱键表：窗口被回收时不留下悬挂引用。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, DragState> States = [];

    /// <summary>
    /// 让 <paramref name="handle"/> 按住时可以拖动 <paramref name="window"/>。
    /// <paramref name="onMoved"/> 在拖动结束时调用一次（用来记住"用户摆放过"）。
    /// </summary>
    public static void Attach(Window window, UIElement handle, Action? onMoved = null)
    {
        var state = new DragState(window, onMoved);
        States.AddOrUpdate(window, state);

        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            e.Handled = true;
            state.Begin();
        };
    }

    /// <summary>诊断用：这个窗口此刻是否正在被拖动（探针核对"按下后进入拖动状态"）。</summary>
    public static bool IsDragging(Window window) =>
        States.TryGetValue(window, out var state) && state.IsDragging;

    private sealed class DragState(Window window, Action? onMoved)
    {
        private bool _dragging;
        private (int X, int Y) _cursor;
        private (int X, int Y) _origin;

        public bool IsDragging => _dragging;

        public void Begin()
        {
            if (!PInvoke.GetCursorPos(out var cursor)) return;

            var origin = CurrentPosition(window);
            if (origin is null) return;

            _cursor = (cursor.X, cursor.Y);
            _origin = origin.Value;
            _dragging = true;

            window.MouseMove += OnMove;
            window.MouseLeftButtonUp += OnUp;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            if (e.LeftButton != MouseButtonState.Pressed) { End(); return; }
            if (!PInvoke.GetCursorPos(out var cursor)) return;

            var hwnd = (HWND)new WindowInteropHelper(window).Handle;
            PInvoke.SetWindowPos(
                hwnd, default,
                _origin.X + (cursor.X - _cursor.X),
                _origin.Y + (cursor.Y - _cursor.Y),
                0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
        }

        private void OnUp(object sender, MouseButtonEventArgs e) => End();

        private void End()
        {
            if (!_dragging) return;
            _dragging = false;
            window.MouseMove -= OnMove;
            window.MouseLeftButtonUp -= OnUp;

            // 拖过就置位：之后不再自动摆位，否则自动摆位会把窗口拽回原处，等于白拖。
            onMoved?.Invoke();
        }

        /// <summary>窗口当前的物理像素位置；句柄还没建好时返回 null。</summary>
        private static (int X, int Y)? CurrentPosition(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return null;

            return PInvoke.GetWindowRect((HWND)hwnd, out var rect)
                ? (rect.left, rect.top)
                : null;
        }
    }
}
