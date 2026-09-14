using System.Text.Json;

namespace WinQuick.Core.Config;

/// <summary>
/// 菜单配置（<c>menus.json</c>）。
///
/// 与 <see cref="MenuSpec"/> 的关系：这里是**文件格式**，那份是运行期模型。
/// 分开是因为文件格式要照顾手写的舒适度（允许缺省、允许注释），
/// 而运行期模型要紧凑且不可为空 —— 直接用手写格式当运行期模型会到处是 null 检查。
/// </summary>
public sealed class MenuConfigFile
{
    public int Version { get; init; } = 1;

    public List<MenuConfigMenu> Menus { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static MenuConfigFile Parse(string json) =>
        JsonSerializer.Deserialize<MenuConfigFile>(json, Options) ?? new MenuConfigFile();
}

public sealed class MenuConfigMenu
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>该菜单专属的热键。为空表示只能用全局热键唤出。</summary>
    public string? Hotkey { get; init; }

    public List<MenuConfigItem> Items { get; init; } = [];
}

public sealed class MenuConfigItem
{
    /// <summary>显示名。必填。</summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// 图标来源。可以是文件路径，也可以是 shell 命名空间路径
    /// （<c>shell:AppsFolder\&lt;AUMID&gt;</c>，UWP 应用只能这么写）。
    /// 留空则画首字占位图。
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// 角度位置（0° = 正上，顺时针）。
    /// **可省略** —— 省略时按项数均匀分布。这比要求手写单调递增的角度友好得多。
    /// 指定时要严格递增，否则扇区会交叠。
    /// </summary>
    public double? Angle { get; init; }

    /// <summary>选中后执行的动作。有子项时应为空。</summary>
    public MenuConfigAction? Action { get; init; }

    /// <summary>子菜单项。有子项时该项点击即展开，不执行动作。</summary>
    public List<MenuConfigItem>? Children { get; init; }
}

public sealed class MenuConfigAction
{
    /// <summary>launch / open / url / shell。默认 launch。</summary>
    public string Type { get; init; } = "launch";

    public string Target { get; init; } = "";

    public string? Args { get; init; }

    public string? WorkingDirectory { get; init; }
}

/// <summary>把文件格式转成运行期模型。转换失败的原因要能说清楚，否则用户改了配置却不知道为什么没生效。</summary>
public static class MenuConfigMapper
{
    public static (MenuSpec? Menu, IReadOnlyList<string> Problems) ToMenu(MenuConfigMenu source)
    {
        var problems = new List<string>();
        var items = new List<MenuItemSpec>(source.Items.Count);

        for (var i = 0; i < source.Items.Count; i++)
        {
            var converted = ToItem(source.Items[i], $"items[{i}]", problems);
            if (converted is not null) items.Add(converted);
        }

        if (items.Count == 0)
            problems.Add($"菜单「{source.Id}」没有任何有效项 —— 拒绝加载，否则会得到一个空轮盘");

        // 扇区数上限：CHI'93 实测 8 扇区是错误率拐点，Kando 的经验值也绝不超过 12。
        if (items.Count > Geometry.RadialLayout.MaxSectors)
            problems.Add($"菜单「{source.Id}」有 {items.Count} 项，超过上限 {Geometry.RadialLayout.MaxSectors}");

        if (problems.Count > 0) return (null, problems);

        return (new MenuSpec(source.Id, source.Name, items), problems);
    }

    private static MenuItemSpec? ToItem(MenuConfigItem source, string path, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(source.Label))
        {
            problems.Add($"{path}.label 为空 —— 每一项都必须有显示名");
            return null;
        }

        var children = new List<MenuItemSpec>();
        if (source.Children is { Count: > 0 })
        {
            for (var i = 0; i < source.Children.Count; i++)
            {
                var child = ToItem(source.Children[i], $"{path}.children[{i}]", problems);
                if (child is not null) children.Add(child);
            }
        }

        if (children.Count > Geometry.RadialLayout.MaxChildSectors)
            problems.Add($"{path}（{source.Label}）有 {children.Count} 个子项，" +
                         $"超过上限 {Geometry.RadialLayout.MaxChildSectors}");

        // 有子项又有动作是配置错误：点击该扇区的语义会变得不确定（展开还是执行？）。
        if (children.Count > 0 && source.Action is not null)
            problems.Add($"{path}（{source.Label}）同时配了子项和动作 —— 点击时无法确定该展开还是执行");

        MenuAction? action = null;
        if (source.Action is { } raw)
        {
            if (string.IsNullOrWhiteSpace(raw.Target))
            {
                problems.Add($"{path}.action.target 为空");
            }
            else
            {
                action = new MenuAction
                {
                    Type = string.IsNullOrWhiteSpace(raw.Type) ? "launch" : raw.Type.Trim().ToLowerInvariant(),
                    Target = raw.Target,
                    Arguments = raw.Args,
                    WorkingDirectory = raw.WorkingDirectory,
                };
            }
        }

        return new MenuItemSpec
        {
            Label = source.Label,
            IconPath = source.Icon,
            Action = action,
            Children = children,
            AngleDegrees = source.Angle,
        };
    }

    /// <summary>校验角度配置：显式角度必须严格递增，否则扇区会交叠、命中测试失去意义。</summary>
    public static IReadOnlyList<string> ValidateAngles(MenuConfigMenu menu)
    {
        var problems = new List<string>();
        CheckLevel(menu.Items, "items", problems);
        return problems;

        static void CheckLevel(List<MenuConfigItem> items, string path, List<string> problems)
        {
            var last = double.NegativeInfinity;
            for (var i = 0; i < items.Count; i++)
            {
                var angle = items[i].Angle;
                if (angle is { } value)
                {
                    var normalized = Geometry.SectorSpan.Normalize(value);
                    if (normalized <= last)
                    {
                        problems.Add(
                            $"{path}[{i}]（{items[i].Label}）的 angle={value} 未严格大于前一项的 {last:0.#}；" +
                            "显式角度必须严格递增。可以省略 angle 让程序均匀分布。");
                    }
                    last = normalized;
                }

                if (items[i].Children is { Count: > 0 } children)
                    CheckLevel(children, $"{path}[{i}].children", problems);
            }
        }
    }
}
