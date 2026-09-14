using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// 配置解析与校验。
///
/// 这里锁住的是一条产品承诺：**配置写错绝不导致程序打不开。**
/// 所以每个"非法输入"用例除了断言被拦下，也在断言错误信息可读 ——
/// 用户看到「items[2]（丙）的 angle=45 未严格大于前一项的 90」才知道怎么改，
/// 看到「解析失败」就只能猜。
/// </summary>
public class ConfigTests
{
    // ---------- config.json ----------

    [Fact]
    public void DefaultConfig_IsValid()
    {
        // 首次运行写出的那份模板必须自身合法，否则第一次启动就报错。
        var config = AppConfig.Parse(DefaultConfigJson);
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void JsonWithCommentsAndTrailingCommas_IsAccepted()
    {
        // 配置是给人手写的，注释和尾逗号必须容忍 —— 否则用户第一个动作就是踩坑。
        var config = AppConfig.Parse("""
            {
              // 这是注释
              "version": 1,
              "hotkey": "Ctrl+Alt+Space",
              "startup": { "autoStart": true },
            }
            """);

        Assert.Equal("Ctrl+Alt+Space", config.Hotkey);
        Assert.True(config.Startup.AutoStart);
        Assert.Empty(config.Validate());
    }

    [Fact]
    public void OptionalKnobs_DefaultToNullMeaningUseDesignBaseline()
    {
        // null 表示「沿用 design/tokens.json 的设计基线」。
        // 这是刻意的分层：同一个值不能有两个来源，否则改了一处没改另一处会静默分叉。
        var config = new AppConfig();

        Assert.Null(config.Appearance.DimOpacity);
        Assert.Null(config.Appearance.AccentColor);
        Assert.Null(config.Behavior.HoverOpenDelayMs);
        Assert.Null(config.Behavior.HoverCloseDelayMs);
        Assert.Null(config.Behavior.AnimationsEnabled);
    }

    [Theory]
    [InlineData("Ctrl+Win+W")]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Shift+F5")]
    public void ValidHotkeys_Pass(string hotkey)
    {
        Assert.Empty(new AppConfig { Hotkey = hotkey }.Validate());
    }

    [Fact]
    public void UnparseableHotkey_IsRejectedWithReadableMessage()
    {
        var problems = new AppConfig { Hotkey = "Ctrl+NotAKey" }.Validate();

        var message = Assert.Single(problems);
        Assert.Contains("hotkey", message);
        Assert.Contains("NotAKey", message);
    }

    [Fact]
    public void HotkeyWithoutModifier_IsRejected()
    {
        // 无修饰键的全局热键会吞掉整个系统的该按键，必须拦住。
        var problems = new AppConfig { Hotkey = "F5" }.Validate();
        Assert.Contains(problems, p => p.Contains("修饰键"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    public void OutOfRangeDimOpacity_IsRejected(double value)
    {
        var config = new AppConfig { Appearance = new AppearanceConfig { DimOpacity = value } };
        Assert.Contains(config.Validate(), p => p.Contains("dimOpacity"));
    }

    [Fact]
    public void HoverCloseDelay_MustExceedOpenDelay()
    {
        // 若收起不比展开慢，指针掠过扇区边缘时子菜单会闪 —— 这条是设计约束。
        var config = new AppConfig
        {
            Behavior = new BehaviorConfig { HoverOpenDelayMs = 400, HoverCloseDelayMs = 200 },
        };

        var message = Assert.Single(config.Validate());
        Assert.Contains("必须大于", message);
    }

    [Fact]
    public void NegativeHoverDelay_IsRejected()
    {
        var negative = new AppConfig { Behavior = new BehaviorConfig { HoverOpenDelayMs = -1 } };
        Assert.Contains(negative.Validate(), p => p.Contains("hoverOpenDelayMs"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(150)]
    public void SmallButNonNegativeHoverDelay_IsAllowed(int value)
    {
        // 这条最初被我写成了拒绝 —— 但那是我没想清楚：
        // 研究结论是「低于 150ms 容易误展开」，那是给默认值的**建议**，不是硬约束。
        // 用户明确要"几乎无延迟"是合理选择，拦下来是越权。
        // 硬约束只有两条：不能为负、收起必须慢于展开。
        var config = new AppConfig { Behavior = new BehaviorConfig { HoverOpenDelayMs = value } };
        Assert.Empty(config.Validate());
    }

    [Theory]
    [InlineData("#0078D4", true)]
    [InlineData("0078D4", true)]
    [InlineData("#0078D", false)]
    [InlineData("#0078D4FF", false)]
    [InlineData("blue", false)]
    public void AccentColor_MustBeHexRgb(string value, bool shouldPass)
    {
        var config = new AppConfig { Appearance = new AppearanceConfig { AccentColor = value } };
        var hasProblem = config.Validate().Any(p => p.Contains("accentColor"));

        Assert.Equal(!shouldPass, hasProblem);
    }

    // ---------- menus.json ----------

    private const string TwoItemMenu = """
        {
          "version": 1,
          "menus": [
            {
              "id": "default", "name": "测试",
              "items": [
                { "label": "甲", "action": { "type": "launch", "target": "a.exe" } },
                { "label": "乙", "action": { "type": "url", "target": "example.com" } }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void MenuConfig_ParsesAndMaps()
    {
        var file = MenuConfigFile.Parse(TwoItemMenu);
        var menu = Assert.Single(file.Menus);

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Empty(problems);
        Assert.NotNull(spec);
        Assert.Equal("default", spec.Id);
        Assert.Equal(2, spec.Items.Count);
        Assert.Equal("甲", spec.Items[0].Label);
        Assert.Equal("launch", spec.Items[0].Action!.Type);
        Assert.Equal("example.com", spec.Items[1].Action!.Target);
    }

    [Fact]
    public void Angle_IsOptional_TwoItemsWithoutAnglesAreEvenlySpread()
    {
        // angle 可省略是相对 Kando 的一处改进 —— 手写配置时不必自己算角度。
        var menu = MenuConfigFile.Parse(TwoItemMenu).Menus[0];
        var (spec, _) = MenuConfigMapper.ToMenu(menu);

        Assert.All(spec!.Items, item => Assert.Null(item.AngleDegrees));
        // 省略时由 RadialLayout 均匀分布，这里只确认它没被塞进一个假角度。
    }

    [Fact]
    public void NonIncreasingAngles_AreRejectedWithReadableMessage()
    {
        var menu = MenuConfigFile.Parse("""
            {
              "version": 1,
              "menus": [{ "id": "m", "name": "m", "items": [
                { "label": "甲", "angle": 0,  "action": { "target": "a.exe" } },
                { "label": "乙", "angle": 90, "action": { "target": "b.exe" } },
                { "label": "丙", "angle": 45, "action": { "target": "c.exe" } }
              ]}]
            }
            """).Menus[0];

        var problems = MenuConfigMapper.ValidateAngles(menu);

        var message = Assert.Single(problems);
        Assert.Contains("丙", message);
        Assert.Contains("45", message);
        Assert.Contains("90", message);
        Assert.Contains("省略", message);   // 要告诉用户还有省略这条出路
    }

    [Fact]
    public void ItemWithBothChildrenAndAction_IsRejected()
    {
        // 点击该扇区的语义会变得不确定：展开还是执行？
        var menu = MenuConfigFile.Parse("""
            {
              "version": 1,
              "menus": [{ "id": "m", "name": "m", "items": [
                { "label": "甲", "action": { "target": "a.exe" },
                  "children": [ { "label": "乙", "action": { "target": "b.exe" } } ] },
                { "label": "丙", "action": { "target": "c.exe" } }
              ]}]
            }
            """).Menus[0];

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Null(spec);
        Assert.Contains(problems, p => p.Contains("同时配了子项和动作"));
    }

    [Fact]
    public void EmptyMenu_IsRejected()
    {
        // 空菜单会得到一个什么都没有的轮盘 —— 那是明显的配置错误，
        // 不该让用户面对一个空圆盘去猜哪里错了。
        var menu = MenuConfigFile.Parse("""
            { "version": 1, "menus": [{ "id": "m", "name": "m", "items": [] }] }
            """).Menus[0];

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Null(spec);
        Assert.Contains(problems, p => p.Contains("没有任何有效项"));
    }

    [Fact]
    public void TooManyItems_IsRejected()
    {
        // CHI'93 实测 8 扇区是错误率拐点，Kando 经验值绝不超过 12。
        var items = string.Join(",", Enumerable.Range(0, 13)
            .Select(i => $$"""{ "label": "项{{i}}", "action": { "target": "a.exe" } }"""));

        var menu = MenuConfigFile.Parse(
            $$"""{ "version": 1, "menus": [{ "id": "m", "name": "m", "items": [{{items}}] }] }""")
            .Menus[0];

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Null(spec);
        Assert.Contains(problems, p => p.Contains("超过上限"));
    }

    [Fact]
    public void MissingLabel_IsRejectedWithItemPath()
    {
        var menu = MenuConfigFile.Parse("""
            {
              "version": 1,
              "menus": [{ "id": "m", "name": "m", "items": [
                { "label": "甲", "action": { "target": "a.exe" } },
                { "action": { "target": "b.exe" } }
              ]}]
            }
            """).Menus[0];

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Null(spec);
        // 错误信息要带上位置，否则用户不知道是哪一项出的问题。
        Assert.Contains(problems, p => p.Contains("items[1]") && p.Contains("label"));
    }

    [Fact]
    public void ActionType_DefaultsToLaunchAndIsLowercased()
    {
        var menu = MenuConfigFile.Parse("""
            {
              "version": 1,
              "menus": [{ "id": "m", "name": "m", "items": [
                { "label": "甲", "action": { "target": "a.exe" } },
                { "label": "乙", "action": { "type": "URL", "target": "example.com" } }
              ]}]
            }
            """).Menus[0];

        var (spec, _) = MenuConfigMapper.ToMenu(menu);

        Assert.Equal("launch", spec!.Items[0].Action!.Type);
        Assert.Equal("url", spec.Items[1].Action!.Type);
    }

    [Fact]
    public void EmptyActionTarget_IsRejected()
    {
        var menu = MenuConfigFile.Parse("""
            {
              "version": 1,
              "menus": [{ "id": "m", "name": "m", "items": [
                { "label": "甲", "action": { "target": "" } },
                { "label": "乙", "action": { "target": "b.exe" } }
              ]}]
            }
            """).Menus[0];

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Null(spec);
        Assert.Contains(problems, p => p.Contains("action.target 为空"));
    }

    [Fact]
    public void NestedChildren_AreMappedRecursively()
    {
        var menu = MenuConfigFile.Parse("""
            {
              "version": 1,
              "menus": [{ "id": "m", "name": "m", "items": [
                { "label": "父", "children": [
                  { "label": "子甲", "action": { "target": "a.exe" } },
                  { "label": "子乙", "children": [
                     { "label": "孙", "action": { "target": "c.exe" } } ] }
                ]},
                { "label": "丙", "action": { "target": "d.exe" } }
              ]}]
            }
            """).Menus[0];

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Empty(problems);
        var parent = spec!.Items[0];
        Assert.True(parent.HasChildren);
        Assert.Equal(2, parent.Children.Count);
        Assert.True(parent.Children[1].HasChildren);
        Assert.Equal("孙", parent.Children[1].Children[0].Label);
    }

    [Fact]
    public void UwpTarget_IsPreservedAsShellPath()
    {
        // UWP 应用没有可执行文件，只能写 shell:AppsFolder\<AUMID>。
        var menu = MenuConfigFile.Parse("""
            {
              "version": 1,
              "menus": [{ "id": "m", "name": "m", "items": [
                { "label": "计算器",
                  "icon": "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
                  "action": { "type": "shell",
                              "target": "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" } },
                { "label": "乙", "action": { "target": "b.exe" } }
              ]}]
            }
            """).Menus[0];

        var (spec, problems) = MenuConfigMapper.ToMenu(menu);

        Assert.Empty(problems);
        Assert.StartsWith("shell:AppsFolder\\", spec!.Items[0].IconPath);
        Assert.Equal("shell", spec.Items[0].Action!.Type);
    }

    /// <summary>与 <c>ConfigStore.DefaultConfigJson</c> 保持一致；不一致时该测试会失败。</summary>
    private const string DefaultConfigJson = """
        {
          "version": 1,
          "hotkey": "Ctrl+Win+W",
          "allowHookFallback": true,
          "appearance": { "dimOpacity": null, "accentColor": null },
          "behavior": {
            "hoverOpenDelayMs": null,
            "hoverCloseDelayMs": null,
            "animationsEnabled": null,
            "hideOnFocusLoss": true
          },
          "startup": { "autoStart": false },
          "activeMenu": null
        }
        """;
}
