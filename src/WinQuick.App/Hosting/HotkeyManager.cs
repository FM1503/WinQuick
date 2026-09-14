using System.Runtime.InteropServices;
using System.Windows.Interop;
using WinQuick.App.Interop;
using WinQuick.Core.Hotkey;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.Hosting;

/// <summary>热键注册走到了哪一层。降级顺序见 <see cref="HotkeyManager"/>。</summary>
public enum HotkeyTier
{
    None = 0,

    /// <summary>用 RegisterHotKey 注册成功 —— 零开销、无钩子、无超时风险。首选。</summary>
    RegisterHotKey,

    /// <summary>RegisterHotKey 失败（多半是 1409 被占），改用低级键盘钩子抢键。</summary>
    LowLevelHook,
}

public sealed record HotkeyApplyResult(
    bool Success,
    HotkeyTier Tier,
    int Win32Error,
    string Message)
{
    /// <summary>
    /// 钩子层特有的不确定性：安装成功 ≠ 按键能到达钩子。
    /// 系统层消费的组合会"静默无效"，只能靠实际按一次来确认。
    /// </summary>
    public bool NeedsLiveVerification => Success && Tier == HotkeyTier.LowLevelHook;
}

/// <summary>
/// 全局热键的三级降级。
///
/// ```
/// RegisterHotKey(组合)          ← 默认路径：零开销、无钩子、无超时风险
///    │ 返回 1409（已被占用）
///    ▼
/// WH_KEYBOARD_LL 钩子接管        ← 吞键 + 幽灵键清残留状态
///    │ 钩子也抢不到（系统层消费，静默无效）
///    ▼
/// 报告失败 + 针对性建议（叠加 Ctrl 往往就能绕开 shell 预留）
/// ```
///
/// 关于第三级：钩子路径**无法通过 API 判断成功与否** ——
/// SetWindowsHookEx 返回成功、钩子也确实装着，但目标组合可能在到达钩子之前
/// 就被系统消费掉（实测 Win+W 不属于此类，物理按键能到达）。
/// 所以 <see cref="NeedsLiveVerification"/> 为真时，必须让用户真按一次来确认。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x5751;

    private readonly HwndSource _messageWindow;
    private readonly KeyboardHook _hook = new();
    private bool _disposed;

    private HotkeySpec? _current;
    private bool _registeredWithApi;

    public HotkeyManager()
    {
        // RegisterHotKey 要求窗口属于调用线程，所以自带一个隐藏的消息窗口收 WM_HOTKEY。
        var parameters = new HwndSourceParameters("WinQuick.Hotkey")
        {
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)WINDOW_STYLE.WS_POPUP),
            ExtendedWindowStyle = 0,
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        _hook.HotkeyPressed += () => Pressed?.Invoke(this, EventArgs.Empty);
    }

    // CsWin32 把句柄类型生成为 internal，所以这里只能是 internal。
    internal HWND Handle => (HWND)_messageWindow.Handle;

    public HotkeyTier Tier { get; private set; }
    public HotkeySpec? Current => _current;

    /// <summary>热键被按下（无论走了哪一层）。在 UI 线程上触发。</summary>
    public event EventHandler? Pressed;

    /// <summary>
    /// 探测某个组合能否用 RegisterHotKey 注册，注册成功立刻注销。
    /// 设置界面的「按下即测」用它给实时反馈，不改变当前生效的热键。
    /// </summary>
    internal static (bool Available, int ErrorCode) Probe(HWND hwnd, HotkeySpec spec)
    {
        var ok = PInvoke.RegisterHotKey(hwnd, HotkeyId, ToWin32Modifiers(spec.Modifiers), spec.VirtualKey);
        var err = ok ? 0 : Marshal.GetLastWin32Error();
        if (ok) PInvoke.UnregisterHotKey(hwnd, HotkeyId);
        return (ok, err);
    }

    /// <summary>
    /// 探测候选组合能否注册，**过程中会临时释放当前热键**，探测完立刻恢复。
    ///
    /// 为什么必须临时释放：如果候选组合正好等于当前生效的那个，
    /// 直接探测会因为"被自己占用"而返回 1409 —— 用户会看到"当前热键被占用"，
    /// 完全莫名其妙。释放再探测才能得到真实结论。
    ///
    /// 代价是在这个过程中热键短暂失效；调用方是设置界面，用户正在手动操作，
    /// 这个窗口期没有问题。
    /// </summary>
    public (bool Available, int ErrorCode) ProbeTemporarily(HotkeySpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var current = _current;
        var currentAllowsHook = current is not null;
        Release();

        try
        {
            return Probe(Handle, spec);
        }
        finally
        {
            // 无论探测结果如何都要把原来的热键装回去 ——
            // 否则用户打开设置界面看一眼、什么都没改，热键却没了。
            if (current is not null) Apply(current, currentAllowsHook);
        }
    }

    /// <summary>把当前热键切成新的组合。失败时保持原样不变。</summary>
    public HotkeyApplyResult Apply(HotkeySpec spec, bool allowHookFallback = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Release();

        // 第一级：RegisterHotKey
        if (PInvoke.RegisterHotKey(Handle, HotkeyId, ToWin32Modifiers(spec.Modifiers), spec.VirtualKey))
        {
            _registeredWithApi = true;
            _current = spec;
            Tier = HotkeyTier.RegisterHotKey;
            return new HotkeyApplyResult(true, Tier, 0, $"已用 RegisterHotKey 注册 {spec}");
        }

        var error = Marshal.GetLastWin32Error();

        if (!allowHookFallback)
        {
            _current = spec;
            Tier = HotkeyTier.None;
            return new HotkeyApplyResult(false, Tier, error, DescribeFailure(spec, error));
        }

        // 第二级：低级键盘钩子
        _hook.TargetVk = spec.VirtualKey;
        _hook.RequireWin = spec.Modifiers.HasFlag(HotkeyModifiers.Win);
        _hook.RequireCtrl = spec.Modifiers.HasFlag(HotkeyModifiers.Control);
        _hook.RequireAlt = spec.Modifiers.HasFlag(HotkeyModifiers.Alt);
        _hook.RequireShift = spec.Modifiers.HasFlag(HotkeyModifiers.Shift);
        _hook.SwallowTarget = true;
        _hook.Install();

        _current = spec;

        if (!_hook.IsInstalled)
        {
            Tier = HotkeyTier.None;
            return new HotkeyApplyResult(false, Tier, error,
                $"RegisterHotKey 失败（err={error}），钩子也未能安装：{_hook.LastError}");
        }

        Tier = HotkeyTier.LowLevelHook;
        return new HotkeyApplyResult(true, Tier, error,
            $"RegisterHotKey 失败（err={error}），已改用低级键盘钩子接管 {spec}。" +
            "需要实际按一次确认按键能到达钩子（系统层消费的组合会静默无效）。");
    }

    private static string DescribeFailure(HotkeySpec spec, int error)
    {
        var reason = error switch
        {
            1409 => $"已被系统或其他程序占用（err=1409）",
            87 => "组合非法（err=87）",
            _ => $"注册失败（err={error}）",
        };
        var hint = HotkeySpec.SuggestIfReserved(spec);
        return hint is null ? reason : reason + "。" + hint;
    }

    /// <summary>注销当前热键（两层都清）。</summary>
    public void Release()
    {
        if (_registeredWithApi)
        {
            PInvoke.UnregisterHotKey(Handle, HotkeyId);
            _registeredWithApi = false;
        }
        _hook.Uninstall();
        Tier = HotkeyTier.None;
    }

    /// <summary>
    /// 健康自检。Win7+ 上钩子回调超时会被系统**静默摘掉**且不通知应用，
    /// 唯一能发现的办法是自己重新安装 —— 这里在钩子已失效时重装。
    /// 由上层定时调用（间隔 5-10 秒即可，回调本身零成本）。
    /// </summary>
    public bool ReinstallHookIfLost()
    {
        if (_disposed || _current is null || Tier != HotkeyTier.LowLevelHook) return false;
        if (_hook.IsInstalled) return false;

        _hook.Install();
        return _hook.IsInstalled;
    }

    /// <summary>钩子被重装过几次 —— 非零说明系统摘掉过钩子，值得记日志。</summary>
    public int HookInstallCount => _hook.InstallCount;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == PInvoke.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            // RegisterHotKey 路径。注意：这里必须**同步**派发，
            // 因为前台权限豁免要求在同一调用栈内完成 SetForegroundWindow。
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static HOT_KEY_MODIFIERS ToWin32Modifiers(HotkeyModifiers modifiers)
    {
        var result = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) result |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) result |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) result |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (modifiers.HasFlag(HotkeyModifiers.Win)) result |= HOT_KEY_MODIFIERS.MOD_WIN;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        _hook.Dispose();
        _messageWindow.Dispose();
    }
}
