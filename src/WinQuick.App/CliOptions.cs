namespace WinQuick.App;

/// <summary>
/// 极简命令行解析：第一个非选项参数是命令，其余是 `--key=value` 或 `--flag`。
/// M0 的探针要能被脚本反复调用，所以参数必须好写。
/// </summary>
public sealed class CliOptions
{
    public string Command { get; private set; } = "";

    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public const string HelpText = """
        用法: WinQuick.exe <命令> [--key=value ...]

          无参数                   启动托盘常驻（开机自启走的也是这条）
          --add-to=值 路径          由文件右键菜单调用：把该路径加进指定目的地
                                      （值形如 root 或 group:分组名）

        探针（--env / --*-probe / --render-preview）已拆到独立进程：
        publish\probes\WinQuick.Probes.exe --help-all
        """;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        if (args.Length == 0) return o;

        // 第一个参数无条件当作命令 —— 即使它长得像 --hook-probe 那样的开关。
        // 之前按"第一个不以 -- 开头的参数"来判断，结果所有命令都被当成 flag，
        // 静默退化到无参数分支。
        o.Command = args[0].ToLowerInvariant();

        // 第一个参数里也允许带 = 值：`WinQuick.exe --add-to=root "文件"`。
        // 不拆的话命令名会变成整串 `--add-to=root`，匹配不到任何 case、
        // 静默落到"未知命令"分支 —— 表现是菜单项注册成功了、点下去什么都没发生。
        // （右键菜单注册的命令行正是这个形状，它是这个问题唯一暴露出来的地方。）
        var firstEq = o.Command.IndexOf('=');
        if (firstEq > 0)
        {
            // 键名要去掉 -- 前缀，与下面那个选项循环保持一致 ——
            // 否则存进去的是 "--add-to"、查的是 "add-to"，取不到值。
            // （这正是探针用真实命令行核对时抓到的：命令对了、值是空的。）
            var name = o.Command[..firstEq];
            if (name.StartsWith("--", StringComparison.Ordinal)) name = name[2..];

            o._values[name] = o.Command[(firstEq + 1)..];
            o.Command = o.Command[..firstEq];
        }

        // 位置参数（不以 -- 开头）也要收下：右键菜单注册的命令行是
        // `WinQuick.exe --add-to="root" "%1"`，那个文件路径就是个位置参数。
        // 原来这里直接把它们丢掉 —— 那样命令看起来注册成功了、点下去却什么都不会发生。
        for (var i = 1; i < args.Length; i++)
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                o.Positionals.Add(args[i]);

        foreach (var raw in args.Skip(1))
        {
            if (!raw.StartsWith("--", StringComparison.Ordinal)) continue;

            var body = raw[2..];
            var eq = body.IndexOf('=');
            if (eq < 0) o._values[body] = null;
            else o._values[body[..eq]] = body[(eq + 1)..];
        }
        return o;
    }

    /// <summary>位置参数（第一个参数之后、不以 -- 开头的那些）。</summary>
    public List<string> Positionals { get; } = [];

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public string GetString(string name, string fallback) => Get(name) ?? fallback;

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), out var v) ? v : fallback;
}
