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

          --env                     打印环境与显示器/DPI 报告（需 DPI-aware 进程才是真值）
          --mem-probe               空闲内存实测：托盘 + 预热窗口 + 隐藏遮罩
              --warmup=秒           稳定等待时长，默认 12
              --cycles=N            唤出/关闭循环次数，默认 0（只测冷启动空闲）
              --visible=秒          唤出后停留秒数，默认 3
          --hook-probe              热键实测：RegisterHotKey 可用性 + LL 钩子能否截获
              --inject              用 SendInput 注入 Win+W 观察钩子是否看到
              --interactive=秒      等待用户物理按键，默认 0（不等待）
          --config-probe            配置系统：默认文件创建、非法配置的容错、合法配置生效
          --apps-probe              枚举 shell:AppsFolder，核对 UWP 应用的 AUMID 与图标
          --action-probe            核对动作执行：Process.Start 是否成功、参数是否传对
          --overlay-probe           核对覆盖层：窗口真实可见性、位置钳制、前台焦点还原、点击扇区
              --hold=N              显示出轮盘并保持 N 秒供肉眼查看（Esc 可关闭）
          --render-preview          离屏渲染出 PNG，做几何与配色的数值自检
              --out=路径            输出 PNG，默认 artifacts/m0/preview.png
              --state=名字          渲染状态，默认 default
        """;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        if (args.Length == 0) return o;

        // 第一个参数无条件当作命令 —— 即使它长得像 --hook-probe 那样的开关。
        // 之前按"第一个不以 -- 开头的参数"来判断，结果所有命令都被当成 flag，
        // 静默退化到无参数分支。
        o.Command = args[0].ToLowerInvariant();

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

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public string GetString(string name, string fallback) => Get(name) ?? fallback;

    public int GetInt(string name, int fallback) =>
        int.TryParse(Get(name), out var v) ? v : fallback;
}
