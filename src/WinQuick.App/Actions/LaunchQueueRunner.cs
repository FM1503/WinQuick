using System.IO;
using WinQuick.App.Diagnostics;
using WinQuick.Core.Config;

namespace WinQuick.App.Actions;

/// <summary>
/// 启动队列的顺序执行器。UI 无关：它只管按节奏逐项启动，结果通过事件交出去，
/// 由调用方（常驻装配）决定怎么展示。
///
/// 三条刻意的设计：
///
/// 1. **固定间隔，不等「就绪」。** 队列最常被用在开机/重启之后，那时系统本就处在高负载：
///    磁盘、杀毒、索引、更新服务全在抢资源，同时起几个程序会互相拖慢，
///    总时长反而更长，且更难看出是哪个程序卡住了。而「等上一步就绪再发下一步」在
///    Windows 上拿不到可靠信号 —— 启动器、闪屏、单实例程序（先起一个引导进程，
///    真正的窗口由已在跑的实例弹出）都会让「进程还在不在」「窗口出来没有」判断失真。
///    固定间隔不完美，但它是确定的、可解释的；真需要更慢的用户自己把 intervalMs 调大。
///
/// 2. **失败跳过，不中止整队。** 队列是一份清单，坏一项废一项已经够了；
///    中止整队等于让「路径写错的那一个」把用户其余九个也拖下水 ——
///    正好违反他配这份清单的意图。
///
/// 3. **同一个队列再触发一次就是「停止」。** 一键启停同一个入口最不容易误操作：
///    用户看到队列跑到一半想叫停时，第一反应就是再点一次那一项；如果那时它反而
///    「从头再来」，正在跑的和新起的会叠在一起，比不响应更糟。
///
/// 4. **文件夹项合并成一个资源管理器窗口。** 队列里「打开目录」的项先由
///    <see cref="LaunchQueuePlanner"/> 拼成一步，一次 explorer 调用打开（Win11 多标签页）。
///    理由与排布规则见 planner 的类注释；执行侧只需要知道「一步可能代表多个条目」，
///    所以成功数按批内条目数累计。程序项完全不受影响，仍按原顺序逐项启动。
/// </summary>
public sealed class LaunchQueueRunner
{
    /// <summary>所有状态判定的唯一入口。事件在锁外触发 —— 处理器里再调 Start 不会死锁。</summary>
    private readonly object _gate = new();

    private CancellationTokenSource? _cancellation;

    /// <summary>
    /// 当前是否有队列在跑（含等待间隔的那段时间）。
    /// 停止之后立刻为 false，**不等**旧线程真正退出 —— 否则用户按下停止、看到状态还是
    /// 「在跑」，再点一次会被判成「同 id」而再次置取消，看上去像没反应。
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return Volatile.Read(ref _running) == 1;
        }
    }

    /// <summary>正在跑（或最近一次跑过的）那套队列的 id；从没跑过为 null。</summary>
    public string? RunningQueueId { get; private set; }

    private int _running;

    // 下面三个事件都在**后台线程**上触发。处理器里不能直接碰 WPF 界面，
    // 需要自行调度到 UI 线程（ResidentHost 用 Dispatcher.CheckAccess + BeginInvoke）。

    /// <summary>后台线程已启动、第一项即将执行。</summary>
    public event Action<LaunchQueueSpec>? Started;

    /// <summary>自然跑完（没有被停止）。参数：队列、成功项数、失败清单（全成功时为 null）。</summary>
    public event Action<LaunchQueueSpec, int, IReadOnlyList<(string Label, string Message)>?>? Finished;

    /// <summary>被用户停止。参数：队列、停止前已执行的项数。「换队列」的静默退出不发此事件。</summary>
    public event Action<LaunchQueueSpec, int>? Stopped;

    /// <summary>启动一套队列；或在它正在跑时把它停掉；或在跑着别的队列时换成这一套。</summary>
    public void Start(LaunchQueueSpec queue)
    {
        CancellationTokenSource? toCancel = null;
        CancellationTokenSource? fresh = null;
        LaunchQueueSpec? stoppedQueue = null;
        var stoppedExecuted = 0;

        lock (_gate)
        {
            var running = _cancellation is not null && Volatile.Read(ref _running) == 1;

            if (running && string.Equals(RunningQueueId, queue.Id, StringComparison.Ordinal))
            {
                // 同 id 再触发 = 停止：本次调用不启动新线程，由这里直接发 Stopped
                // （旧线程在下一个取消检查点静默退出，见 RunLoop）。已执行的项数在这里
                // 取快照 —— 旧线程收尾时还会再 ++ 一次，不能让提示里的数字跟着变。
                stoppedQueue = _current ?? queue;
                stoppedExecuted = _executed;
                toCancel = _cancellation;
                Close();
            }
            else
            {
                if (running)
                {
                    // 换队列：旧线程静默退出（用户并没有叫停，只是换了另一套），新队列接上。
                    toCancel = _cancellation;
                    Close();
                }

                fresh = new CancellationTokenSource();
                _cancellation = fresh;
                _current = queue;
                _executed = 0;
                RunningQueueId = queue.Id;
                Volatile.Write(ref _running, 1);
            }
        }

        // 取消与启动线程都放在锁外：Cancel 会同步执行注册的回调，
        // 在锁内做有死锁风险；而事件处理器里再调 Start 也是同一条道理。
        toCancel?.Cancel();

        if (stoppedQueue is not null)
        {
            Stopped?.Invoke(stoppedQueue, stoppedExecuted);
            return;
        }

        // 事件在锁外发；Started 在 RunLoop 里作为线程的第一个动作发出。
        ThreadPool.QueueUserWorkItem(_ => RunLoop(queue, fresh!));
    }

    /// <summary>
    /// open 且目标是现存目录，才算文件夹批的成员。
    ///
    /// 目标不存在的不进批：多个路径混进一次 explorer 调用时，坏路径让整体行为不可预期
    /// （而且用户只得到一句笼统的错），还不如按原路逐项走，拿到明确的「找不到目标」。
    /// </summary>
    private static bool IsFolderItem(MenuItemSpec item) =>
        item.Action is { Type: "open" } action
        && !string.IsNullOrWhiteSpace(action.Target)
        && Directory.Exists(action.Target);

    /// <summary>按步骤执行。<see cref="ActionExecutor.ExecuteSync"/> 自己兜住异常，这里只管节奏与汇总。</summary>
    private void RunLoop(LaunchQueueSpec queue, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;

        Started?.Invoke(queue);

        // 排布在开始前一次算定：执行途中文件夹被删掉也不会让步骤中途变化
        // （否则「末项之后不等待」的判断会跟着漂移）。
        var steps = LaunchQueuePlanner.ToSteps(queue.Items, IsFolderItem);

        var failures = new List<(string Label, string Message)>();
        var succeeded = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            if (token.IsCancellationRequested) return;

            var step = steps[i];
            ActionResult result;
            string label;
            int stepSucceeded;

            switch (step)
            {
                case LaunchStep.Single single:
                    label = single.Item.Label;
                    result = ActionExecutor.ExecuteSync(single.Item);
                    stepSucceeded = 1;
                    break;

                case LaunchStep.Folders folders:
                    // 一步代表 N 个条目：成功时 N 个都算成功（用户配了 3 个目录，
                    // 汇总里就该看到 3 个起来了），失败时只记一条并写明数量。
                    label = folders.Items.Count == 1
                        ? folders.Items[0].Label
                        : $"{folders.Items.Count} 个文件夹";
                    result = ActionExecutor.ExecuteFoldersBatch(folders.Items);
                    stepSucceeded = folders.Items.Count;
                    break;

                default:
                    continue;
            }

            lock (_gate) _executed += stepSucceeded;

            if (result.Success)
            {
                succeeded += stepSucceeded;
            }
            else
            {
                // 失败项统一在这里留痕：ExecuteSync 刻意不触发 Completed，
                // 否则一次启动 10 个程序会弹 10 次气球，而用户要的信息只在汇总里。
                failures.Add((label, result.Message));
                CrashLog.Write($"启动队列项失败: {queue.Name}/{label}",
                    new InvalidOperationException(result.Message));
            }

            // 末项之后的间隔没有意义：等完也只是结束，用户会觉得「都起来了还在等什么」。
            if (i == steps.Count - 1) break;

            if (queue.IntervalMs > 0)
            {
                // 可取消的等待而不是 Thread.Sleep：停止要立刻生效，
                // 否则用户按了停止还要再等最长 5 秒才见反应。
                using var waited = new ManualResetEventSlim(false);
                using var registration = token.Register(waited.Set);
                waited.Wait(queue.IntervalMs);
            }

            if (token.IsCancellationRequested) return;
        }

        lock (_gate)
        {
            // 已被换队列或停止接管：状态是别人的了，不要覆盖，也不要发事件。
            if (!ReferenceEquals(_cancellation, cancellation)) return;
            Close();
        }

        Finished?.Invoke(queue, succeeded, failures.Count == 0 ? null : failures);
    }

    /// <summary>调用方必须持有 <see cref="_gate"/>。</summary>
    private void Close()
    {
        _cancellation = null;
        _current = null;
        Volatile.Write(ref _running, 0);
    }

    private LaunchQueueSpec? _current;
    private int _executed;
}
