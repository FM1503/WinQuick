using System.Collections.Concurrent;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Interop;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）。用来抢占 RegisterHotKey 拿不到的组合，
/// 典型就是被系统占用或 shell 抢注的 Win+某键。
///
/// 三条硬性纪律，全部来自官方文档与 PowerToys 的实现：
///
/// 1. 回调里只做匹配和吞键，其余一切（日志、IO、UI）都 PostMessage 出去。
///    官方明确：回调超时会被系统静默摘掉钩子，且"没有办法让应用知道"。
///
/// 2. 修饰键必须靠自己维护的状态机判断，不能用 GetAsyncKeyState 判断"正在处理的那个键"。
///    官方原文：回调早于异步按键状态更新。
///
/// 3. 吞了 Win 的 down 就必须吞 Win 的 up。否则系统看到的是一次"只有抬起、没有按下"的
///    Win 事件，会按"轻点 Win 键"处理 —— 开始菜单就这么弹出来的。
///    触发后还要注入一个不存在的 VK 0xFF keyup，清掉系统侧残留的按下状态
///    （这是 PowerToys centralized_kb_hook.cpp 的做法）。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    // 修饰键与目标键的虚拟键码。CsWin32 把 VK_* 生成在 VIRTUAL_KEY 枚举里，
    // 用局部常量更直白，也避免枚举成员名的版本差异。
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4;
    private const uint VK_RMENU = 0xA5;

    public readonly record struct KeyEvent(
        uint VkCode, bool IsDown, bool IsInjected,
        bool Win, bool Ctrl, bool Alt, bool Shift);

    // 回调只往这里塞，绝不直接调用可能阻塞的代码。
    private readonly ConcurrentQueue<KeyEvent> _observed = new();

    private readonly HOOKPROC _proc;      // 必须持有强引用，否则委托被 GC 后钩子失效
    private HHOOK _hook;
    private bool _disposed;

    // 修饰键状态机 —— 不信 GetAsyncKeyState，自己记账。
    private bool _win, _ctrl, _alt, _shift;

    // 已吞下的目标键，它的 up 也必须吞，否则会留下按键状态不一致。
    private uint _swallowedKeyDown;
    private bool _swallowWinUp;

    public KeyboardHook()
    {
        _proc = HookProc;
    }

    public uint TargetVk { get; set; }
    public bool RequireWin { get; set; }
    public bool RequireCtrl { get; set; }
    public bool RequireAlt { get; set; }
    public bool RequireShift { get; set; }

    /// <summary>测试模式可关掉吞键，只为观察按键是否到达钩子。</summary>
    public bool SwallowTarget { get; set; } = true;

    /// <summary>
    /// 探测专用保险：无条件吞掉 Win 的 up。
    /// 正常情况下只有"吞过的组合"才需要吞 Win-up；但在探测场景里如果目标键压根没到钩子
    /// （正是我们要验证的那种失败），Win-up 就会漏出去，系统按"轻点 Win"处理、开始菜单弹出。
    /// 打开这个开关可以保证探测过程不打扰用户。
    /// </summary>
    public bool AlwaysSwallowWinUp { get; set; }

    public bool IsInstalled => _hook != default;

    public string? LastError { get; private set; }

    public event Action? HotkeyPressed;

    /// <summary>成功安装的钩子实例数，用来判断是否被系统摘掉后重装过。</summary>
    public int InstallCount { get; private set; }

    public unsafe void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsInstalled) return;

        var hmod = PInvoke.GetModuleHandle((string?)null);
        _hook = PInvoke.SetWindowsHookEx(
            WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _proc, new HINSTANCE(hmod.DangerousGetHandle()), 0);

        if (_hook == default)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            LastError = $"SetWindowsHookEx 失败，GetLastError={err}";
        }
        else
        {
            InstallCount++;
            LastError = null;
        }
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        PInvoke.UnhookWindowsHookEx(_hook);
        _hook = default;
        ResetKeyState();
    }

    private void ResetKeyState()
    {
        _win = _ctrl = _alt = _shift = false;
        _swallowedKeyDown = 0;
        _swallowWinUp = false;
    }

    /// <summary>把回调期间攒下的观察记录搬走。只能在 UI 线程调用。</summary>
    public List<KeyEvent> DrainObserved()
    {
        var list = new List<KeyEvent>(_observed.Count + 4);
        while (_observed.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    private unsafe LRESULT HookProc(int nCode, WPARAM wParam, LPARAM lParam)
    {
        // 官方要求：nCode < 0 时必须原样往下传，不能做任何处理。
        if (nCode < 0)
            return PInvoke.CallNextHookEx(default, nCode, wParam, lParam);

        var msg = (uint)wParam.Value;
        var isDown = msg is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
        var isUp = msg is PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP;
        if (!isDown && !isUp)
            return PInvoke.CallNextHookEx(default, nCode, wParam, lParam);

        var kb = (KBDLLHOOKSTRUCT*)lParam.Value;
        var vk = kb->vkCode;
        var injected = (kb->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0;

        var swallow = false;

        switch (vk)
        {
            case VK_LWIN:
            case VK_RWIN:
                _win = isDown;
                // Win 的 down 不吞：否则单独按 Win 开开始菜单的功能就废了。
                if (isUp && (_swallowWinUp || AlwaysSwallowWinUp))
                {
                    swallow = true;
                    _swallowWinUp = false;
                }
                break;

            case VK_LCONTROL:
            case VK_RCONTROL:
                _ctrl = isDown;
                break;

            case VK_LMENU:
            case VK_RMENU:
                _alt = isDown;
                break;

            case VK_LSHIFT:
            case VK_RSHIFT:
                _shift = isDown;
                break;

            default:
                if (isDown && vk == TargetVk && ModifiersMatch())
                {
                    if (SwallowTarget)
                    {
                        swallow = true;
                        _swallowedKeyDown = vk;
                        _swallowWinUp = _win;   // 吞过的组合必须把 Win up 一起吞掉
                    }
                    RaisePressed();
                }
                else if (isUp && vk == _swallowedKeyDown)
                {
                    swallow = true;
                    _swallowedKeyDown = 0;
                }
                break;
        }

        if (vk != 0)
            _observed.Enqueue(new KeyEvent(vk, isDown, injected, _win, _ctrl, _alt, _shift));

        return swallow
            ? new LRESULT(1)
            : PInvoke.CallNextHookEx(default, nCode, wParam, lParam);
    }

    private bool ModifiersMatch() =>
        _win == RequireWin && _ctrl == RequireCtrl &&
        _alt == RequireAlt && _shift == RequireShift;

    /// <summary>
    /// 触发动作。钩子线程不是 UI 线程，所以走 Dispatcher 投递，绝不在这里直接干活。
    /// 另外触发后注入一个 VK 0xFF 的 keyup —— 这个"幽灵键"不存在，没有副作用，
    /// 只用来把系统侧残留的 Win 按下状态擦干净。
    /// </summary>
    private void RaisePressed()
    {
        SendGhostKeyUp();

        var handler = HotkeyPressed;
        if (handler is null) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) handler();
        else dispatcher.BeginInvoke(DispatcherPriority.Send, handler);
    }

    private static unsafe void SendGhostKeyUp()
    {
        Span<INPUT> inputs = stackalloc INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)0xFF,
                    dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP,
                },
            },
        };
        PInvoke.SendInput(inputs, sizeof(INPUT));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }
}
