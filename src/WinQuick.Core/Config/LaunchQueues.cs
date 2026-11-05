using System.Text.Json;

namespace WinQuick.Core.Config;

/// <summary>
/// 启动队列配置（<c>launch-queues.json</c>）。
///
/// 与 <see cref="MenuConfigFile"/> 同构：这里是**文件格式**，<see cref="LaunchQueueSpec"/> 是运行期模型。
/// 分开的理由也一样 —— 文件格式要照顾手写舒适度（允许缺省、允许注释），
/// 运行期模型要紧凑且不可为空。
/// </summary>
public sealed class LaunchQueueConfigFile
{
    public int Version { get; init; } = 1;

    public List<LaunchQueueFile> Queues { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static LaunchQueueConfigFile Parse(string json) =>
        JsonSerializer.Deserialize<LaunchQueueConfigFile>(json, Options) ?? new LaunchQueueConfigFile();
}

public sealed class LaunchQueueFile
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>相邻两项之间的间隔（毫秒）。0 表示同时启动，靠执行器自己限速。</summary>
    public int IntervalMs { get; init; } = LaunchQueuesMapper.DefaultIntervalMs;

    public List<LaunchQueueItemFile> Items { get; init; } = [];
}

public sealed class LaunchQueueItemFile
{
    public string Label { get; init; } = "";

    public string? Icon { get; init; }

    /// <summary>复用菜单的动作类型：队列项要做的事与菜单项完全一样（launch / open / url / shell）。</summary>
    public MenuConfigAction? Action { get; init; }
}

/// <summary>
/// 一套命名启动队列。队列项直接复用 <see cref="MenuItemSpec"/> —— 执行器拿到的就是「要启动什么」，
/// 与菜单项同形，App 侧不需要为两种来源写两条执行路径。
/// </summary>
public sealed record LaunchQueueSpec(
    string Id,
    string Name,
    int IntervalMs,
    IReadOnlyList<MenuItemSpec> Items);

/// <summary>
/// 文件格式与运行期模型之间的映射与校验。
///
/// 校验策略是「尽量保底加载，问题记录在案」，与菜单的整单拒绝**刻意不同**：
/// 队列是一份执行清单，用户配了 10 个要启动的程序，其中一个目标写错，
/// 整单拒绝等于让他别的 9 个也启动不了。坏一项只废一项，其余照常跑。
/// </summary>
public static class LaunchQueuesMapper
{
    /// <summary>单套队列的条目上限。再多已超出「一次启动」的意义，且执行窗口会被拖得很长。</summary>
    public const int MaxItems = 32;

    public const int MinIntervalMs = 0;

    public const int MaxIntervalMs = 5000;

    public const int DefaultIntervalMs = 500;

    /// <summary>
    /// 序列化用的选项。与 ConfigStore 的 MenuJsonOptions 同一条理由：
    /// 这份文件是给人手写/编辑的，键名要与其它配置一致（camelCase），
    /// 中文标签不能被转成 <c>\uXXXX</c>（满屏码点看不出是哪个字）。
    /// </summary>
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static (IReadOnlyList<LaunchQueueSpec> Queues, IReadOnlyList<string> Problems) ToQueues(
        LaunchQueueConfigFile file)
    {
        var problems = new List<string>();
        var queues = new List<LaunchQueueSpec>(file.Queues.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < file.Queues.Count; i++)
        {
            var source = file.Queues[i];
            var id = source.Id ?? "";
            var where = $"queues[{i}]";

            if (string.IsNullOrWhiteSpace(id))
            {
                problems.Add($"{where}.id 为空 —— 跳过该队列");
                continue;
            }

            if (!seenIds.Add(id))
            {
                problems.Add($"{where}.id「{id}」与前面的队列重复 —— 跳过该队列");
                continue;
            }

            // 名称只用于展示，为空不值得报错（报出来用户也只是看到一句噪音），直接用 id 兜底。
            var name = string.IsNullOrWhiteSpace(source.Name) ? id : source.Name;

            var interval = source.IntervalMs;
            if (interval < MinIntervalMs)
            {
                interval = MinIntervalMs;
                problems.Add($"{where}（{id}）.intervalMs={source.IntervalMs} 小于 {MinIntervalMs} —— 已改为 {interval}");
            }
            else if (interval > MaxIntervalMs)
            {
                interval = MaxIntervalMs;
                problems.Add($"{where}（{id}）.intervalMs={source.IntervalMs} 大于 {MaxIntervalMs} —— 已改为 {interval}");
            }

            var items = new List<MenuItemSpec>();
            var rawItems = source.Items ?? [];
            for (var j = 0; j < rawItems.Count; j++)
            {
                var converted = ToItem(rawItems[j], $"{where}.items[{j}]", problems);
                if (converted is not null) items.Add(converted);
            }

            if (items.Count > MaxItems)
            {
                problems.Add($"{where}（{id}）有 {items.Count} 条有效项，超过上限 {MaxItems} —— 只保留前 {MaxItems} 条");
                items.RemoveRange(MaxItems, items.Count - MaxItems);
            }

            // 空队列是合法状态，原样保留且不报问题：编辑器里「新建后、加条目前」就是空的，
            // 托盘里显示「名（0 项）」已足够自解释，报问题只会把正常流程当故障
            // （每次热重载一条崩溃日志、改配置时还弹气泡）。
            // 保留而不是丢弃：直接丢掉的话它会在列表里凭空消失。
            queues.Add(new LaunchQueueSpec(id, name, interval, items));
        }

        return (queues, problems);
    }

    private static MenuItemSpec? ToItem(
        LaunchQueueItemFile source, string path, List<string> problems)
    {
        // 定位优先用 label（用户认得的是名字），没有 label 才退回下标。
        var locate = string.IsNullOrWhiteSpace(source.Label) ? path : $"{path}（{source.Label}）";

        if (string.IsNullOrWhiteSpace(source.Label))
        {
            problems.Add($"{locate}.label 为空 —— 跳过该条目");
            return null;
        }

        if (source.Action is not { } raw)
        {
            problems.Add($"{locate}.action 缺失 —— 跳过该条目");
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw.Target))
        {
            problems.Add($"{locate}.action.target 为空 —— 跳过该条目");
            return null;
        }

        return new MenuItemSpec
        {
            Label = source.Label,
            IconPath = source.Icon,
            Action = new MenuAction
            {
                Type = string.IsNullOrWhiteSpace(raw.Type) ? "launch" : raw.Type.Trim().ToLowerInvariant(),
                Target = raw.Target,
                Arguments = raw.Args,
                WorkingDirectory = raw.WorkingDirectory,
            },
        };
    }

    /// <summary>
    /// 往返映射：运行期模型 → 文件格式。
    /// 只搬队列概念里存在的字段 —— <see cref="MenuItemSpec.AngleDegrees"/> 与 Children 是轮盘布局的产物，
    /// 写进队列文件只会让手写者以为它们有意义。
    /// </summary>
    public static LaunchQueueConfigFile ToFile(IReadOnlyList<LaunchQueueSpec> queues) => new()
    {
        Version = 1,
        Queues = queues.Select(q => new LaunchQueueFile
        {
            Id = q.Id,
            Name = q.Name,
            IntervalMs = q.IntervalMs,
            Items = q.Items.Select(item => new LaunchQueueItemFile
            {
                Label = item.Label,
                Icon = item.IconPath,
                Action = item.Action is { } a
                    ? new MenuConfigAction
                    {
                        Type = a.Type,
                        Target = a.Target,
                        Args = a.Arguments,
                        WorkingDirectory = a.WorkingDirectory,
                    }
                    : null,
            }).ToList(),
        }).ToList(),
    };

    public static string Serialize(LaunchQueueConfigFile file) =>
        JsonSerializer.Serialize(file, FileJsonOptions);
}
