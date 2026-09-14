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

        var cli = CliOptions.Parse(CommandLineArgs());

        switch (cli.Command)
        {
            case "--env":
                M0.EnvProbe.Run();
                ExitSoon();
                break;

            // 下面两个探针需要消息循环在跑（DispatcherTimer / await Task.Delay），
            // 但 OnStartup 阶段 WPF 的调度器还没开始泵消息，
            // 所以必须投递出去、等 Run() 把循环跑起来之后再执行。
            case "--mem-probe":
                RunDetached(() => M0.MemoryProbe.RunAsync(cli));
                break;

            case "--hook-probe":
                RunDetached(() => M0.HookProbe.RunAsync(cli));
                break;

            case "--config-probe":
                // 需要消息循环：热重载靠 DispatcherTimer 触发防抖。
                RunDetached(() => M0.ConfigProbe.RunAsync(cli));
                break;

            case "--apps-probe":
                M0.AppsProbe.Run(cli);
                ExitSoon();
                break;

            case "--action-probe":
                RunDetached(() => M0.ActionProbe.RunAsync(cli));
                break;

            case "--overlay-probe":
                // 需要消息循环（Show/Hide 之后要等实际合成），走投递路径。
                RunDetached(() => M0.OverlayProbe.RunAsync(cli));
                break;

            case "--render-preview":
                M0.RenderPreview.Run(cli);
                ExitSoon();
                break;

            case "":
                StartResident();
                break;

            default:
                Diagnostics.ProbeOutput.Initialize("usage");
                Diagnostics.ProbeOutput.Line($"未知命令: {cli.Command}");
                Diagnostics.ProbeOutput.Line(CliOptions.HelpText);
                Diagnostics.ProbeOutput.Flush();
                ExitSoon();
                break;
        }
    }

    /// <summary>
    /// 托盘常驻模式：无参数启动时的默认路径。开机自启走的也是这条。
    /// </summary>
    private void StartResident()
    {
        _resident = new Hosting.ResidentHost();

        if (!_resident.IsFirstInstance)
        {
            // 已经有实例在跑：唤醒它把覆盖层带出来，然后安静退出。
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
