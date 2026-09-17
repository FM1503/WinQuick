using System.Windows;

namespace WinQuick.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 常驻模式（托盘）不创建主窗口，所以不能让 WPF 自己决定何时退出。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 常驻托盘程序崩掉是用户最不能接受的失败形态（热键突然失灵、还没人知道）。
        // 兜底把异常落盘，至少让排查有据可依。
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("DispatcherUnhandledException", args.Exception);
            Diagnostics.ProbeOutput.Line();
            Diagnostics.ProbeOutput.Line("!! 调度器未处理异常: " + args.Exception);
            Diagnostics.ProbeOutput.Flush();
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        // 令牌资源必须在任何窗口创建之前装好：XAML 里用 DynamicResource 引用它们，
        // 缺了会在创建窗口时抛异常。放在这里也保证了探针模式同样可用。
        Design.TokenResources.Install();

        var cli = CliOptions.Parse(CommandLineArgs());

        switch (cli.Command)
        {
            case "--add-to":
                // 文件右键菜单调用的入口。**不创建任何窗口、不弹东西** ——
                // 用户只是想加一项，不该因此看到任何界面。
                RunAddToRequest(cli);
                break;

            case "":
                StartResident();
                break;

            default:
                Diagnostics.ProbeOutput.Initialize("usage");
                Diagnostics.ProbeOutput.Line($"未知命令: {cli.Command}");
                Diagnostics.ProbeOutput.Line(CliOptions.HelpText);
                Diagnostics.ProbeOutput.Line();
                Diagnostics.ProbeOutput.Line("探针（env / 各 *-probe / render-preview）不在本 exe 里，" +
                                             "见 publish 目录下 probes 子目录中的 WinQuick.Probes.exe。");
                Diagnostics.ProbeOutput.Flush();
                ExitSoon();
                break;
        }
    }

    /// <summary>
    /// 把右键菜单的请求交给常驻实例。
    ///
    /// 本进程**不改 menus.json**：那样会和常驻实例的设置保存互相覆盖
    /// （最后写入者赢，用户刚在设置面板里做的改动会被悄悄回退）。
    /// 请求落在配置目录里，常驻实例本来就在监视那个目录（400ms 防抖），会自己拾取。
    ///
    /// 已有实例在跑 → 留下请求后安静退出；
    /// 没有实例在跑 → 自己常驻起来，启动流程会把这条请求应用掉（否则它会一直躺着）。
    /// </summary>
    private void RunAddToRequest(CliOptions cli)
    {
        var value = cli.GetString("add-to", "");
        var path = cli.Positionals.FirstOrDefault() ?? "";

        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(path))
        {
            CrashLog.Write("右键菜单请求不完整",
                new InvalidOperationException($"add-to=「{value}」路径=「{path}」"));
            Shutdown(0);
            return;
        }

        var store = new Config.ConfigStore();
        var written = store.TryWriteAddRequest(
            new Core.Config.ShellAddRequest(value, path), out var error);

        if (!written)
        {
            CrashLog.Write("右键菜单请求写盘失败",
                new InvalidOperationException(error ?? "未知原因"));
            Shutdown(0);
            return;
        }

        // 刻意**不走** StartResident 的"唤醒已有实例"分支：那个信号会让常驻实例
        // 弹出设置面板，而这里只需要它顺手读一下请求文件。
        if (Hosting.SingleInstance.IsAnotherInstanceRunning())
        {
            Shutdown(0);
            return;
        }

        StartResident();
    }

    /// <summary>
    /// 托盘常驻模式：无参数启动时的默认路径。开机自启走的也是这条。
    /// </summary>
    private void StartResident()
    {
        _resident = new Hosting.ResidentHost();

        if (!_resident.IsFirstInstance)
        {
            // 已经有实例在跑：唤醒它打开设置界面，然后安静退出。
            // 不弹错误框 —— 用户双击图标期望的是"看到界面"，不是"看到一条提示"。
            Hosting.SingleInstance.SignalExistingInstance();
            Shutdown(0);
            return;
        }

        _resident.Start();
    }

    private Hosting.ResidentHost? _resident;

    protected override void OnExit(ExitEventArgs e)
    {
        _resident?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 取自 Environment 而不是 StartupEventArgs.Args。
    /// SDK 为 WPF 生成的 Main 调的是无参 app.Run()，那条路径下 StartupEventArgs.Args 是空的，
    /// 命令行分发会静默退化成"无参数"分支。Environment 这条永远可靠。
    /// </summary>
    private static string[] CommandLineArgs()
    {
        var all = Environment.GetCommandLineArgs();
        return all.Length <= 1 ? [] : all.Skip(1).ToArray();
    }

    /// <summary>
    /// 把异步探针投递到调度器上跑。
    /// 包一层 try/catch 是因为 async void 抛出的异常会直接终结进程、丢掉已采集的数据 ——
    /// 探针失败也要把数据落盘。
    /// </summary>
    private void RunDetached(Func<Task> work)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Diagnostics.ProbeOutput.Line();
                Diagnostics.ProbeOutput.Line("探针未捕获异常: " + ex);
                Diagnostics.ProbeOutput.Flush();
            }
            Shutdown(0);
        });
    }

    /// <summary>
    /// 同步探针（不需要消息循环）走这里退出。
    /// 直接调 Shutdown 可能早于 Run 启动消息循环，所以推迟到调度器空转时。
    /// </summary>
    private void ExitSoon() => Dispatcher.BeginInvoke(() => Shutdown(0));
}
