using System.Runtime.InteropServices;
using System.Windows.Interop;
using WinQuick.App.Diagnostics;
using WinQuick.App.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.M0;

/// <summary>
/// M0.4 —— 本次调研里唯一一个「网上证据互相矛盾、只能靠实测」的结论：Win+W 到底能不能用。
///
/// 矛盾点：
///   whkd #6 说即使设了 NoWinKeys，Win+W 在 Win11 上仍然拉起小组件面板；
///   AHK 社区又说 #w::return 用低级钩子能拦住。
/// 两者都不可信，所以这里分两步取证据：
///   第一步 RegisterHotKey 逐个试注册 —— 成功即「这个组合是自由的」，无需任何钩子。
///   第二步 装 LL 钩子并（可选）注入 Win+W，看钩子到底能看见哪些按键。
///          看见 VK_W 说明钩子能拦；只看见 Win 键而看不见 W，说明 W 在到达钩子前
///          就被系统消费了，钩子方案无解。
/// </summary>
public static class HookProbe
{
    private const uint VK_W = 0x57;
    private const uint VK_LWIN = 0x5B;
    private const int WM_HOTKEY_ID = 0x5700;

    private static readonly (string Name, HOT_KEY_MODIFIERS Mods, uint Vk, string Note)[] Candidates =
    [
        ("Win+W",          HOT_KEY_MODIFIERS.MOD_WIN,                                 VK_W, "用户原始需求；Win11 小组件抢占嫌疑"),
        ("Ctrl+Win+W",     HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_WIN, VK_W, "本方案默认值"),
        ("Alt+Win+W",      HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_WIN,     VK_W, ""),
        ("Shift+Win+W",    HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_WIN,   VK_W, ""),
        ("Win+A",          HOT_KEY_MODIFIERS.MOD_WIN,                                 0x41, "对照组：Win11 快速设置，预期被占"),
        ("Win+F1",         HOT_KEY_MODIFIERS.MOD_WIN,                                 0x70, "对照组：预期自由"),
        ("Win+F13",        HOT_KEY_MODIFIERS.MOD_WIN,                                 0x7C, "极端对照组：几乎不可能被任何程序使用"),
        ("Ctrl+Alt+Win+W", HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT
                         | HOT_KEY_MODIFIERS.MOD_WIN,                                 VK_W, "极端对照组：三重修饰键"),
        ("Ctrl+Shift+W",   HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_SHIFT, VK_W, "无 Win 键对照组"),
    ];

    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("hook");
        ProbeOutput.Line("本探针分两步：先试注册，再装钩子观察实际按键到达情况。");

        ProbeOutput.Section("运行时");
        ProbeOutput.Line(NativeDisplay.DescribeEnvironment());

        var hwndSource = CreateMessageWindow();
        if (hwndSource is null)
        {
            ProbeOutput.Line("无法创建消息窗口，探测中止。");
            ProbeOutput.Flush();
            return;
        }

        try
        {
            ProbeHotkeyRegistration(hwndSource);

            var inject = cli.Has("inject");
            var interactiveSeconds = cli.GetInt("interactive", 0);

            if (!inject && interactiveSeconds <= 0)
            {
                ProbeOutput.Line();
                ProbeOutput.Line("未指定 --inject 或 --interactive，跳过钩子步骤。");
                return;
            }

            await ProbeHookAsync(inject, interactiveSeconds);
        }
        finally
        {
            ProbeOutput.Flush();
        }
    }

    private static void ProbeHotkeyRegistration(HwndSource hwndSource)
    {
        ProbeOutput.Section("第一步：RegisterHotKey 试注册");
        ProbeOutput.Line("  失败并带 1409 (ERROR_HOTKEY_ALREADY_REGISTERED) = 已被系统或其他程序占用");
        ProbeOutput.Line("  注意：注册成功不等于『按下就有反应』—— 系统层消费的组合可能假成功");
        ProbeOutput.Line();

        var hwnd = (HWND)hwndSource.Handle;
        var free = new List<string>();

        foreach (var (name, mods, vk, note) in Candidates)
        {
            var ok = PInvoke.RegisterHotKey(hwnd, WM_HOTKEY_ID, mods, vk);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            if (ok) PInvoke.UnregisterHotKey(hwnd, WM_HOTKEY_ID);

            var verdict = ok ? "可用" : $"不可用 (err={err}{(err == 1409 ? " = 已被占用" : "")})";
            var suffix = note.Length > 0 ? $"    // {note}" : "";
            ProbeOutput.Kv(name, $"{verdict}{suffix}");
            if (ok) free.Add(name);
        }

        ProbeOutput.Line();
        ProbeOutput.Line("  可注册成功的组合: " + (free.Count > 0 ? string.Join(", ", free) : "(无)"));
    }

    private static async Task ProbeHookAsync(bool inject, int interactiveSeconds)
    {
        ProbeOutput.Section("第二步：WH_KEYBOARD_LL 钩子实测");
        ProbeOutput.Line("  判定标准：钩子若能看到 VK_W(=0x57) 且此时 Win 键为按下状态，");
        ProbeOutput.Line("            说明钩子能在 shell 之前拦到该组合，吞键方案成立。");
        ProbeOutput.Line("            若只看到 Win 键、看不到 W，说明 W 在到达钩子前已被系统消费。");
        ProbeOutput.Line();

        using var hook = new KeyboardHook
        {
            TargetVk = VK_W,
            RequireWin = true,
            SwallowTarget = true,          // 能吞就吞，避免测试时真的触发系统行为
            AlwaysSwallowWinUp = true,     // 保证探测过程不弹开始菜单
        };

        var fired = 0;
        hook.HotkeyPressed += () => Interlocked.Increment(ref fired);
        hook.Install();

        if (!hook.IsInstalled)
        {
            ProbeOutput.Kv("钩子安装", "失败");
            ProbeOutput.Kv("错误", hook.LastError);
            return;
        }

        ProbeOutput.Kv("钩子安装", "成功（WH_KEYBOARD_LL）");
        ProbeOutput.Kv("监听目标", "Win + W");
        ProbeOutput.Line();

        if (inject)
        {
            ProbeOutput.Line("  inject：1.5 秒后注入 Win down / W down / W up / Win up");
            ProbeOutput.Line("  注入事件带 LLKHF_INJECTED 标志，与物理按键未必完全等价。");
            ProbeOutput.Line("  要确认物理按键行为，用 --hook-probe --interactive=15 手动按一次。");
        }
        if (interactiveSeconds > 0)
            ProbeOutput.Line($"  interactive={interactiveSeconds}：请在计时内物理按下一次 Win+W。");
        ProbeOutput.Line();

        var observed = new List<KeyboardHook.KeyEvent>();
        var started = DateTime.UtcNow;
        var injectAt = started.AddMilliseconds(1500);
        var injectedAt = DateTime.MaxValue;
        var injected = false;
        var deadline = interactiveSeconds > 0
            ? started.AddSeconds(interactiveSeconds)
            : started.AddSeconds(4);

        while (true)
        {
            await Task.Delay(80);
            observed.AddRange(hook.DrainObserved());

            var now = DateTime.UtcNow;

            if (inject && !injected && now >= injectAt)
            {
                InjectWinW();
                injected = true;
                injectedAt = now;
            }

            // 注入后留 700ms 收尾，或等到交互期限。
            if (injected && now > injectedAt.AddMilliseconds(700)) break;
            if (interactiveSeconds > 0 && now > deadline) break;
            if (interactiveSeconds <= 0 && !inject && now > deadline) break;
            if (now > started.AddSeconds(20)) break;   // 兜底，绝不无限等
        }

        observed.AddRange(hook.DrainObserved());
        Report(hook, observed, fired);
        hook.Uninstall();
    }

    private static void Report(KeyboardHook hook, List<KeyboardHook.KeyEvent> observed, int fired)
    {
        ProbeOutput.Section("钩子观察到的按键事件");
        if (observed.Count == 0)
        {
            ProbeOutput.Line("  (什么都没看到 —— 钩子没收到任何按键)");
        }
        else
        {
            ProbeOutput.Line($"  {"VK",-5} {"方向",-5} {"Win",-4} {"Ctrl",-5} {"Alt",-4} {"Shift",-6} 注入");
            foreach (var e in observed)
            {
                ProbeOutput.Line($"  0x{e.VkCode:X2}  {(e.IsDown ? "down" : "up"),-5} " +
                                 $"{(e.Win ? "Y" : "."),-4} {(e.Ctrl ? "Y" : "."),-5} " +
                                 $"{(e.Alt ? "Y" : "."),-4} {(e.Shift ? "Y" : "."),-6} " +
                                 $"{(e.IsInjected ? "Y" : ".")}");
            }
        }

        var sawWinDown = observed.Any(e => e.IsDown && (e.VkCode == VK_LWIN || e.VkCode == 0x5C));
        var sawWDown = observed.Any(e => e.IsDown && e.VkCode == VK_W);
        var sawWWithWin = observed.Any(e => e.IsDown && e.VkCode == VK_W && e.Win);

        ProbeOutput.Section("结论");
        ProbeOutput.Kv("钩子看到 Win 按下", sawWinDown ? "是" : "否");
        ProbeOutput.Kv("钩子看到 W 按下", sawWDown ? "是" : "否");
        ProbeOutput.Kv("看到 W 时 Win 处于按下", sawWWithWin ? "是" : "否");
        ProbeOutput.Kv("热键回调触发次数", fired);
        ProbeOutput.Kv("钩子安装次数", hook.InstallCount);
        ProbeOutput.Line();

        if (sawWWithWin)
        {
            ProbeOutput.Line("  >>> Win+W 能被低级键盘钩子截获，吞键方案成立。");
            ProbeOutput.Line("      用户若坚持用 Win+W，可以走钩子路径实现。");
        }
        else if (sawWinDown && !sawWDown)
        {
            ProbeOutput.Line("  >>> 钩子看得到 Win 键但看不到 W —— W 在到达低级钩子之前就被系统消费。");
            ProbeOutput.Line("      Win+W 无法用钩子抢占，只能靠「禁用小组件」释放这个组合。");
        }
        else
        {
            ProbeOutput.Line("  >>> 钩子什么都没看到。可能是注入路径与物理路径不同，");
            ProbeOutput.Line("      请用 --hook-probe --interactive=15 手动按一次 Win+W 再判断。");
        }
    }

    /// <summary>注入 Win down / W down / W up / Win up。</summary>
    private static unsafe void InjectWinW()
    {
        Span<INPUT> inputs = stackalloc INPUT[4];
        inputs[0] = MakeKey(VK_LWIN, up: false);
        inputs[1] = MakeKey(VK_W, up: false);
        inputs[2] = MakeKey(VK_W, up: true);
        inputs[3] = MakeKey(VK_LWIN, up: true);
        PInvoke.SendInput(inputs, sizeof(INPUT));

        static INPUT MakeKey(uint vk, bool up) => new()
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)vk,
                    dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                },
            },
        };
    }

    /// <summary>
    /// RegisterHotKey 要求窗口属于调用线程，所以探针必须自己建一个消息窗口。
    /// WM_HOTKEY 会投递到这里（本探针只关心注册是否成功，不做处理）。
    /// </summary>
    private static HwndSource? CreateMessageWindow()
    {
        try
        {
            var src = new HwndSource(new HwndSourceParameters("WinQuick.HookProbe")
            {
                Width = 1,
                Height = 1,
                PositionX = -32000,
                PositionY = -32000,
                WindowStyle = unchecked((int)WINDOW_STYLE.WS_POPUP),
                ExtendedWindowStyle = 0,
            });
            src.AddHook((IntPtr _, int _, IntPtr _, IntPtr _, ref bool _) => IntPtr.Zero);
            return src;
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("创建消息窗口失败: " + ex.Message);
            return null;
        }
    }
}
