using System.Windows;
using WinQuick.App;
using WinQuick.App.Diagnostics;
using WinQuick.App.M0;   // 探针仍在 WinQuick.App.M0 命名空间（搬迁时刻意不改，见类注释）

namespace WinQuick.Probes;

/// <summary>
/// 探针宿主。
///
/// 探针文件的命名空间仍是 <c>WinQuick.App.M0</c> —— 搬迁时刻意不动它们（只换程序集），
/// 这样这次拆分是纯接线改动，探针逻辑一行未改；它们的可访问性靠 App 项目的
/// <c>InternalsVisibleTo</c> 维持。
///
/// 用法：WinQuick.Probes.exe --xxx-probe [--key=value ...]
/// </summary>
internal static class ProbesHost
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 探针要创建真实窗口（部分还要跑消息循环），所以需要一个 Application 实例：
        // 既提供 Dispatcher，也让 Application.Current 可用（个别探针会用到）。
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        app.DispatcherUnhandledException += (_, e) =>
        {
            CrashLog.Write("探针 DispatcherUnhandledException", e.Exception);
            ProbeOutput.Line();
            ProbeOutput.Line("!! 调度器未处理异常: " + e.Exception);
            ProbeOutput.Flush();
            e.Handled = true;
        };

        // 令牌资源必须在任何窗口创建之前装好：XAML 里用 DynamicResource 引用它们。
        WinQuick.App.Design.TokenResources.Install();

        var cli = CliOptions.Parse(args);

        app.Startup += (_, _) => Dispatch(app, cli);
        app.Run();
    }

    private static void Dispatch(Application app, CliOptions cli)
    {
        switch (cli.Command)
        {
            case "--env":
                EnvProbe.Run();
                ExitSoon(app);
                break;

            // 下面这些需要消息循环在跑（DispatcherTimer / await Task.Delay），
            // 但 Startup 阶段调度器还没开始泵消息，所以必须投递出去、等 Run() 跑起来之后再执行。
            case "--mem-probe":
                RunDetached(app, () => MemoryProbe.RunAsync(cli));
                break;

            case "--hook-probe":
                RunDetached(app, () => HookProbe.RunAsync(cli));
                break;

            case "--zorder-probe":
                RunDetached(app, () => ZOrderProbe.RunAsync(cli));
                break;

            case "--settings-probe":
                RunDetached(app, () => SettingsProbe.RunAsync(cli));
                break;

            case "--config-probe":
                RunDetached(app, () => ConfigProbe.RunAsync(cli));
                break;

            case "--apps-probe":
                // 同步探针：自己跑完就返回，不走投递。
                AppsProbe.Run(cli);
                ExitSoon(app);
                break;

            case "--action-probe":
                RunDetached(app, () => ActionProbe.RunAsync(cli));
                break;

            case "--particle-probe":
                // 需要消息循环：帧间隔靠 CompositionTarget.Rendering 采。
                RunDetached(app, () => ParticleProbe.RunAsync(cli));
                break;

            case "--label-probe":
                RunDetached(app, () => LabelProbe.RunAsync(cli));
                break;

            case "--shell-probe":
                // 同步探针：内部会写 HKCU 并逐键核对，跑完才返回。
                ShellProbe.Run(cli);
                ExitSoon(app);
                break;

            case "--perf-probe":
                RunDetached(app, () => PerfProbe.RunAsync(cli));
                break;

            case "--flicker-probe":
                RunDetached(app, () => FlickerProbe.RunAsync(cli));
                break;

            case "--flash-probe":
                RunDetached(app, () => FlashProbe.RunAsync(cli));
                break;

            case "--add-sector-probe":
                RunDetached(app, () => AddSectorProbe.RunAsync(cli));
                break;

            case "--overlay-probe":
                RunDetached(app, () => OverlayProbe.RunAsync(cli));
                break;

            case "--render-preview":
                RenderPreview.Run(cli);
                ExitSoon(app);
                break;

            default:
                ProbeOutput.Initialize("usage");
                ProbeOutput.Line(cli.Command.Length == 0 ? "未指定探针" : $"未知命令: {cli.Command}");
                ProbeOutput.Line(ProbeHelpText);
                ProbeOutput.Flush();
                ExitSoon(app);
                break;
        }
    }

    /// <summary>
    /// 把异步探针投递到调度器上跑。
    /// 包一层 try/catch 是因为 async void 抛出的异常会直接终结进程、丢掉已采集的数据 ——
    /// 探针失败也要把数据落盘。
    /// </summary>
    private static void RunDetached(Application app, Func<Task> work)
    {
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                ProbeOutput.Line();
                ProbeOutput.Line("探针未捕获异常: " + ex);
                ProbeOutput.Flush();
            }

            app.Shutdown(0);
        });
    }

    /// <summary>同步探针（不需要消息循环）走这里退出。直接 Shutdown 可能早于消息循环启动。</summary>
    private static void ExitSoon(Application app) =>
        app.Dispatcher.BeginInvoke(() => app.Shutdown(0));

    /// <summary>探针帮助文本。产品 exe 不再广告这些开关（见项目文件里的说明）。</summary>
    private const string ProbeHelpText = """
        用法: WinQuick.Probes.exe <探针> [--key=value ...]

        环境与能力
          --env                     打印环境与显示器/DPI 报告（需 DPI-aware 进程才是真值）
          --mem-probe               空闲内存实测：托盘 + 预热窗口 + 隐藏遮罩
              --warmup=秒           稳定等待时长，默认 12
              --cycles=N            唤出/关闭循环次数，默认 0（只测冷启动空闲）
              --visible=秒          唤出后停留秒数，默认 3
          --hook-probe              热键实测：RegisterHotKey 可用性 + LL 钩子能否截获
              --inject              用 SendInput 注入 Win+W 观察钩子是否看到
              --interactive=秒      等待用户物理按键，默认 0（不等待）
          --apps-probe              枚举 shell:AppsFolder，核对 UWP 应用的 AUMID 与图标
          --config-probe            配置系统：默认文件创建、非法配置的容错、合法配置生效
          --action-probe            核对动作执行：Process.Start 是否成功、参数是否传对

        覆盖层与分层窗口
          --overlay-probe           核对覆盖层：窗口真实可见性、位置钳制、前台焦点还原、点击扇区
              --hold=N              显示出轮盘并保持 N 秒供肉眼查看（Esc 可关闭）
          --zorder-probe            核对真实 z 序与落点（WindowFromPoint），诊断"看得见却点不动"
          --add-sector-probe        核对环里的「＋」扇区：命中、点击分派、12 项上限、加项后位置
          --flicker-probe           刷新闪烁：逐动作数窗口级操作，纯内容刷新应当一项都不涨
          --flash-probe             闪烁本身：抓屏幕像素，量某个动作到底闪不闪

        面板、标签与动画
          --settings-probe          设置界面：XAML 加载、保存路径、留空语义、非法输入反馈
          --label-probe             标签：超长文本的截断方式、悬停是否位移、中心读出完整文字
          --perf-probe              帧间隔分布：扫过扇区与子弧动画，测流畅度的可比值
              --ms=N                每个扇区停留毫秒，默认 90
              --repeat=N            重复轮数，默认 3
          --particle-probe          粒子动画代价：在真窗口上逐帧动 N 个图形/一张位图，量帧间隔
              --seconds=N           每档采样秒数，默认 3
          --render-preview          离屏渲染出 PNG，做几何与配色的数值自检
              --out=路径            输出 PNG，默认 artifacts/m0/preview.png
              --state=名字          渲染状态，默认 default

        右键菜单集成
          --shell-probe             注册→逐键核对→注销后无残留（会写 HKCU，结束后自动还原）
        """;
}
