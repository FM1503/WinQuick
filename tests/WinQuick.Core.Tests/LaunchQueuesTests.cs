using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// 启动队列的解析与校验。
///
/// 这里锁住的是一条与菜单**相反**的产品承诺：队列坏了就**尽量保底加载**。
/// 队列是执行清单 —— 10 项里坏 1 项，整单拒绝等于让另外 9 项也启动不了，
/// 所以每个「非法输入」用例除了断言该项被跳过，也断言**其余项照常保留**，
/// 且 problem 文案能定位到具体是哪一条。
/// </summary>
public class LaunchQueuesTests
{
    private static string ValidJson => """
        {
          "version": 1,
          "queues": [
            {
              "id": "work",
              "name": "开工",
              "intervalMs": 300,
              "items": [
                { "label": "VS Code", "icon": "C:\\Tools\\code.exe", "action": { "type": "launch", "target": "code.exe" } },
                { "label": "工作目录", "action": { "type": "open", "target": "D:\\Work" } }
              ]
            },
            {
              "id": "web",
              "name": "浏览器",
              "items": [
                { "label": "文档", "action": { "type": "url", "target": "https://example.com" } }
              ]
            }
          ]
        }
        """;

    private static LaunchQueueConfigFile Parse(string json) => LaunchQueueConfigFile.Parse(json);

    private static string Actions(IEnumerable<string> problems) => string.Join("\n", problems);

    // ---------------------------------------------------------------- 合法文件

    [Fact]
    public void ValidFile_MapsAllFields()
    {
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse(ValidJson));

        Assert.Empty(problems);
        Assert.Equal(2, queues.Count);

        var work = queues[0];
        Assert.Equal("work", work.Id);
        Assert.Equal("开工", work.Name);
        Assert.Equal(300, work.IntervalMs);
        Assert.Equal(2, work.Items.Count);

        Assert.Equal("VS Code", work.Items[0].Label);
        Assert.Equal(@"C:\Tools\code.exe", work.Items[0].IconPath);
        Assert.Equal("launch", work.Items[0].Action?.Type);
        Assert.Equal("code.exe", work.Items[0].Action?.Target);
        Assert.Null(work.Items[0].Action?.Arguments);
        Assert.Null(work.Items[0].Action?.WorkingDirectory);

        Assert.Equal("工作目录", work.Items[1].Label);
        Assert.Null(work.Items[1].IconPath);
        Assert.Equal("open", work.Items[1].Action?.Type);
        Assert.Equal(@"D:\Work", work.Items[1].Action?.Target);

        var web = queues[1];
        Assert.Equal("web", web.Id);
        // 省略 intervalMs 时取默认值，而不是 0（0 意味着全部同时启动，用户多半没这个意思）。
        Assert.Equal(LaunchQueuesMapper.DefaultIntervalMs, web.IntervalMs);
        Assert.Equal("url", web.Items.Single().Action?.Type);
    }

    [Fact]
    public void JsonWithCommentsAndTrailingCommas_IsAccepted()
    {
        // 这份文件是给人手写的，注释与尾逗号必须容忍 —— 否则用户第一个动作就是踩坑。
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse("""
            {
              // 早上开工用的一套
              "queues": [
                {
                  "id": "morning",
                  "items": [
                    { "label": "邮件", "action": { "type": "launch", "target": "outlook.exe" } },
                  ],
                },
              ],
            }
            """));

        Assert.Empty(problems);
        var queue = Assert.Single(queues);
        Assert.Equal("morning", queue.Id);
        Assert.Equal("邮件", queue.Items.Single().Label);
    }

    // ---------------------------------------------------------------- 队列级

    [Fact]
    public void EmptyId_QueueIsSkippedAndRecorded()
    {
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse("""
            {
              "queues": [
                { "id": "", "items": [ { "label": "A", "action": { "target": "a.exe" } } ] },
                { "id": "ok", "items": [ { "label": "B", "action": { "target": "b.exe" } } ] }
              ]
            }
            """));

        // 跳过坏的那套，好的照常保留 —— 这正是「保底加载」与菜单整单拒绝的区别。
        Assert.Equal("ok", Assert.Single(queues).Id);
        Assert.True(problems.Any(p => p.Contains("queues[0].id 为空")), Actions(problems));
    }

    [Fact]
    public void DuplicateId_KeepsFirstOccurrence()
    {
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse("""
            {
              "queues": [
                { "id": "dup", "name": "先出现", "items": [ { "label": "A", "action": { "target": "a.exe" } } ] },
                { "id": "dup", "name": "后出现", "items": [ { "label": "B", "action": { "target": "b.exe" } } ] }
              ]
            }
            """));

        var queue = Assert.Single(queues);
        Assert.Equal("先出现", queue.Name);
        Assert.Equal("A", queue.Items.Single().Label);

        // 问题里要能看出是「哪个位置与哪个位置」重复，否则用户不知道删哪一套。
        Assert.True(problems.Any(p => p.Contains("queues[1]") && p.Contains("dup")), Actions(problems));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(6000, 5000)]
    public void IntervalOutOfRange_IsClampedAndRecorded(int raw, int expected)
    {
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse($$"""
            {
              "queues": [
                { "id": "q", "intervalMs": {{raw}}, "items": [ { "label": "A", "action": { "target": "a.exe" } } ] }
              ]
            }
            """));

        Assert.Equal(expected, Assert.Single(queues).IntervalMs);

        // 钳位是静默改用户的数字，必须留下原值与钳后值，否则用户会以为配置没生效。
        Assert.True(
            problems.Any(p => p.Contains($"intervalMs={raw}") && p.Contains(expected.ToString())),
            Actions(problems));
    }

    [Fact]
    public void EmptyName_FallsBackToId_Silently()
    {
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse("""
            {
              "queues": [
                { "id": "night", "items": [ { "label": "A", "action": { "target": "a.exe" } } ] }
              ]
            }
            """));

        // 名称只是展示用，为空不值得报错（报出来只是噪音），用 id 兜底即可。
        Assert.Empty(problems);
        Assert.Equal("night", Assert.Single(queues).Name);
    }

    // ---------------------------------------------------------------- 条目级

    [Fact]
    public void BadItems_AreSkippedOneByOne_OthersSurvive()
    {
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse("""
            {
              "queues": [
                {
                  "id": "mixed",
                  "items": [
                    { "label": "", "action": { "target": "a.exe" } },
                    { "label": "没动作" },
                    { "label": "没目标", "action": { "target": "  " } },
                    { "label": "好的", "action": { "target": "b.exe" } }
                  ]
                }
              ]
            }
            """));

        var queue = Assert.Single(queues);
        Assert.Equal("好的", queue.Items.Single().Label);

        Assert.True(problems.Any(p => p.Contains("items[0].label 为空")), Actions(problems));
        Assert.True(problems.Any(p => p.Contains("items[1]（没动作）") && p.Contains("action 缺失")), Actions(problems));
        Assert.True(problems.Any(p => p.Contains("items[2]（没目标）") && p.Contains("target 为空")), Actions(problems));
    }

    [Fact]
    public void MoreThanMaxItems_IsTruncatedAndRecorded()
    {
        var items = string.Join(",\n", Enumerable.Range(0, 40)
            .Select(i => $$"""{ "label": "项{{i}}", "action": { "target": "p{{i}}.exe" } }"""));

        var json = "{ \"queues\": [ { \"id\": \"big\", \"items\": [" + items + "] } ] }";
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse(json));

        var queue = Assert.Single(queues);
        Assert.Equal(LaunchQueuesMapper.MaxItems, queue.Items.Count);
        // 保留的是**前** 32 条且顺序不变，不是任意 32 条 —— 顺序就是启动顺序。
        Assert.Equal("项0", queue.Items[0].Label);
        Assert.Equal("项31", queue.Items[^1].Label);

        Assert.True(problems.Any(p => p.Contains("40") && p.Contains("32")), Actions(problems));
    }

    [Fact]
    public void AllItemsInvalid_QueueIsKeptAsEmpty()
    {
        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse("""
            { "queues": [ { "id": "hollow", "items": [ { "label": "x" } ] } ] }
            """));

        // 空队列保留而不是丢弃：用户还能在界面上看到它、修它。
        var queue = Assert.Single(queues);
        Assert.Empty(queue.Items);
        // 且不为「队列为空」报问题：空队列是合法状态（新建后、加条目前），
        // 报出来只会把正常流程当故障。条目自身的问题（这里缺 action）仍照常记录。
        Assert.DoesNotContain(problems, p => p.Contains("hollow") && p.Contains("有效项"));
        Assert.True(problems.Any(p => p.Contains("items[0]（x）.action 缺失")), Actions(problems));
    }

    // ---------------------------------------------------------------- 往返

    [Fact]
    public void RoundTrip_PreservesChineseAndWindowsPaths()
    {
        var original = new LaunchQueueSpec(
            "work",
            "开工",
            250,
            [
                new MenuItemSpec
                {
                    Label = "注册表编辑器",
                    IconPath = @"C:\Windows\System32\regedit.exe",
                    Action = new MenuAction
                    {
                        Type = "launch",
                        Target = @"D:\工具\启动\code.exe",
                        Arguments = "--new-window \"D:\\Work\\WinQuick\"",
                        WorkingDirectory = @"D:\Work\WinQuick",
                    },
                },
                new MenuItemSpec
                {
                    Label = "文档",
                    Action = new MenuAction { Type = "url", Target = "https://example.com/路径" },
                },
            ]);

        var json = LaunchQueuesMapper.Serialize(LaunchQueuesMapper.ToFile([original]));

        // 中文不能被转成 \uXXXX 转义 —— 这份文件是给人读的。
        Assert.Contains("开工", json);
        Assert.Contains(@"D:\\工具\\启动\\code.exe", json);

        var (queues, problems) = LaunchQueuesMapper.ToQueues(Parse(json));
        Assert.Empty(problems);

        var back = Assert.Single(queues);
        // 逐项比对而不是直接比 record：Items 的运行时类型（数组 vs List）不属于语义，比进去只会制造假失败。
        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.IntervalMs, back.IntervalMs);
        Assert.Equal(original.Items, back.Items);
        Assert.Equal("注册表编辑器", back.Items[0].Label);
        Assert.Equal(@"C:\Windows\System32\regedit.exe", back.Items[0].IconPath);
        Assert.Equal(@"D:\Work\WinQuick", back.Items[0].Action?.WorkingDirectory);
        Assert.Equal("https://example.com/路径", back.Items[1].Action?.Target);
    }

    [Fact]
    public void ToFile_DoesNotCarryRadialOnlyFields()
    {
        // AngleDegrees / Children 是轮盘布局的产物，写进队列文件会让手写者以为它们有意义。
        var file = LaunchQueuesMapper.ToFile([
            new LaunchQueueSpec("q", "Q", 500, [
                new MenuItemSpec
                {
                    Label = "带角度",
                    AngleDegrees = 45,
                    Action = MenuAction.Launch("a.exe"),
                },
            ]),
        ]);

        var json = LaunchQueuesMapper.Serialize(file);
        Assert.DoesNotContain("angle", json);
        Assert.DoesNotContain("children", json);
    }
}
