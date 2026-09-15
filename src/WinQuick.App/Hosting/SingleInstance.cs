using System.Threading;
using Microsoft.Win32;

namespace WinQuick.App.Hosting;

/// <summary>
/// 单实例守护。
///
/// 用命名 Mutex + 命名事件：第二个实例启动时不弹错误框，而是唤醒已有实例
/// （让它把设置界面带到前台），然后自己安静退出 —— 用户双击两次图标，
/// 期望的是"看到窗口"，不是"看到一个已经在运行的提示"。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\WinQuick.SingleInstance";
    private const string ActivateEventName = @"Local\WinQuick.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly CancellationTokenSource _cts = new();
    private bool _owned;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out _owned);
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
    }

    /// <summary>本进程是否是唯一的实例。</summary>
    public bool IsFirstInstance => _owned;

    /// <summary>启动后台线程等待"请激活"信号。</summary>
    public void ListenForActivation(Action onActivate)
    {
        var token = _cts.Token;
        var handle = _activateEvent;

        var thread = new Thread(() =>
        {
            var waitHandles = new WaitHandle[] { handle, token.WaitHandle };
            while (!token.IsCancellationRequested)
            {
                var index = WaitHandle.WaitAny(waitHandles);
                if (index != 0 || token.IsCancellationRequested) return;
                onActivate();
            }
        })
        {
            IsBackground = true,
            Name = "WinQuick.ActivationListener",
        };
        thread.Start();
    }

    /// <summary>
    /// 是否已有实例在跑 —— **不发任何信号**。
    ///
    /// 与 <see cref="SignalExistingInstance"/> 的区别很关键：那个信号会让常驻实例
    /// 打开设置面板。从右键菜单加一项时弹设置面板是打扰，所以这条路径只判断、不唤醒。
    /// </summary>
    public static bool IsAnotherInstanceRunning()
    {
        try
        {
            using var existing = Mutex.OpenExisting(MutexName);
            // 互斥体存在还不够：只有它**被持有**才说明进程还活着。
            return !existing.WaitOne(0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>通知已在运行的实例激活。返回是否成功投递。</summary>
    public static bool SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
            {
                using (handle) return handle.Set();
            }
        }
        catch
        {
            // 打不开就当作没有实例在跑 —— 不该因为唤醒失败而阻止用户。
        }
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { /* 未持有就释放会抛异常，忽略 */ }
            _owned = false;
        }
        _mutex.Dispose();
        _activateEvent.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// 开机自启。
///
/// 用 HKCU\...\Run 而不是计划任务：不需要管理员权限，用户能在任务管理器
/// "启动"页里看到并管理它 —— 透明比快 60 秒更重要。
/// 关闭时必须把值真正删掉，不能只写空字符串（否则会留一条启动项，
/// 用户在任务管理器里会看到一个启动失败的幽灵条目）。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinQuick";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    /// <summary>把当前的 exe 路径写进启动项。返回是否成功。</summary>
    public static bool Enable(string? executablePath = null)
    {
        var path = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            // 路径必须加引号，否则带空格的路径（Program Files）会被拆成多个参数。
            key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is null) return true;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled) => enabled ? Enable() : Disable();
}
