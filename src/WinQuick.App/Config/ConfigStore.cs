using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using WinQuick.App.Diagnostics;
using WinQuick.Core.Config;

namespace WinQuick.App.Config;

public sealed record ConfigReloadResult(
    bool ConfigOk,
    bool MenuOk,
    IReadOnlyList<string> Problems)
{
    public bool AllOk => ConfigOk && MenuOk;

    public string Summary => Problems.Count == 0
        ? "配置已生效"
        : string.Join("；", Problems.Take(3)) + (Problems.Count > 3 ? $"（共 {Problems.Count} 条）" : "");
}

/// <summary>
/// 配置的读写与热重载。
///
/// 三条刻意的行为：
///
/// 1. **配置出错绝不导致启动失败。** 手写 JSON 出错太容易了（少个逗号、多个括号）。
///    解析失败时保留上一份有效配置并报告问题 —— 让用户"改错了配置就再也打不开程序"
///    是最糟糕的失败方式。
///
/// 2. **首次运行写出一份带注释的默认配置**，而不是空模板。解析器支持 JSONC，
///    所以注释是可行的，而注释能让"这个字段是干什么的"直接在文件里看到，
///    不必去翻文档。menus.json 则直接序列化当前装配好的菜单 —— 用户拿到的是一份
///    填好真实路径、可以直接改的配置。
///
/// 3. **热重载带防抖。** 编辑器保存一个文件经常触发多次变更事件（先清空再写入），
///    不做防抖会读到写了一半的文件、报一堆假错误。
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private const int ReloadDebounceMs = 400;

    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <param name="directoryOverride">
    /// 配置目录。默认 %APPDATA%\WinQuick；探针会传一个临时目录，
    /// 好在不碰用户真实配置的前提下验证"配置写坏会怎样"。
    /// </param>
    public ConfigStore(string? directoryOverride = null)
    {
        _directory = directoryOverride ?? DefaultDirectory;

        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ReloadDebounceMs),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinQuick");

    private readonly string _directory;

    public string Directory => _directory;
    public string ConfigPath => Path.Combine(_directory, "config.json");
    public string MenusPath => Path.Combine(_directory, "menus.json");

    public AppConfig Config { get; private set; } = new();
    public MenuSpec Menu { get; private set; } = MenuSpec.BuiltInDefault;
    public IReadOnlyList<string> LastProblems { get; private set; } = [];

    /// <summary>配置或菜单发生变化并已生效。UI 与热键需要据此重新应用。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 本次加载时是否新建了默认配置文件。
    /// 用来判断"要不要把配置里的开机自启意图写进注册表" —— 只在首次运行时写一次。
    ///
    /// 不能每次启动都按配置写注册表：用户也可能从托盘菜单切换自启，
    /// 那时注册表变了而配置没变，下次启动又会被配置覆盖回去 —— 表现为"关掉自启后又自己开了"。
    /// </summary>
    public bool DefaultsWereCreated { get; private set; }

    /// <summary>加载，必要时创建默认文件。返回本次加载的结果。</summary>
    public ConfigReloadResult LoadOrCreateDefaults()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath, DefaultConfigJson, Utf8NoBom);
                DefaultsWereCreated = true;
            }

            if (!File.Exists(MenusPath))
            {
                File.WriteAllText(MenusPath, BuildDefaultMenusJson(), Utf8NoBom);
                DefaultsWereCreated = true;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("创建默认配置失败", ex);
            // 写不进去（目录只读、权限受限）也要能继续跑，只是每次启动都用内置默认值。
        }

        return Reload();
    }

    /// <summary>重新读取。失败时保留上一份有效值，只报告问题。</summary>
    public ConfigReloadResult Reload()
    {
        var problems = new List<string>();
        var configOk = true;
        var menuOk = true;

        // ---- config.json ----
        try
        {
            var text = File.ReadAllText(ConfigPath);
            var parsed = AppConfig.Parse(text);
            var validation = parsed.Validate();

            if (validation.Count > 0)
            {
                configOk = false;
                problems.AddRange(validation.Select(p => "config.json: " + p));
            }
            else
            {
                Config = parsed;
            }
        }
        catch (Exception ex)
        {
            configOk = false;
            problems.Add($"config.json 解析失败：{ex.Message}（保留上一份有效配置）");
        }

        // ---- menus.json ----
        try
        {
            var text = File.ReadAllText(MenusPath);
            var file = MenuConfigFile.Parse(text);

            var wantedId = Config.ActiveMenu;
            var chosen = file.Menus.FirstOrDefault(m => m.Id == wantedId)
                         ?? file.Menus.FirstOrDefault();

            if (chosen is null)
            {
                menuOk = false;
                problems.Add("menus.json 里没有任何菜单（保留上一份有效菜单）");
            }
            else
            {
                var menuProblems = new List<string>(MenuConfigMapper.ValidateAngles(chosen));
                var (menu, mapProblems) = MenuConfigMapper.ToMenu(chosen);
                menuProblems.AddRange(mapProblems);

                if (menu is null || menuProblems.Count > 0)
                {
                    menuOk = false;
                    problems.AddRange(menuProblems.Select(p => "menus.json: " + p));
                }
                else
                {
                    Menu = menu;
                }
            }
        }
        catch (Exception ex)
        {
            menuOk = false;
            problems.Add($"menus.json 解析失败：{ex.Message}（保留上一份有效菜单）");
        }

        LastProblems = problems;

        if (problems.Count > 0)
            CrashLog.Write("配置有问题", new InvalidOperationException(string.Join("\n", problems)));

        Changed?.Invoke(this, EventArgs.Empty);
        return new ConfigReloadResult(configOk, menuOk, problems);
    }

    /// <summary>开始监视两个配置文件的变化。</summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;

        try
        {
            _watcher = new FileSystemWatcher(Directory, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            // 变更事件经常连续触发（编辑器先清空再写），所以只重置防抖计时器。
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
        }
        catch (Exception ex)
        {
            // 监视失败不影响使用，只是改配置后需要重启程序。
            CrashLog.Write("启动配置监视失败", ex);
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        // 每次变更都重新开始计时，只有安静满 400ms 才真正重载。
        // 编辑器保存一个文件经常连续触发多次事件（先清空再写入），
        // 不防抖会读到写了一半的文件并报出一堆假错误。
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>把当前菜单写成 menus.json（供设置界面导出用）。</summary>
    public string SerializeMenu(MenuSpec menu)
    {
        var file = new MenuConfigFile
        {
            Version = 1,
            Menus = [ToConfig(menu)],
        };
        return JsonSerializer.Serialize(file, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            // 生成的 menus.json 是给人手写/编辑的，键名风格必须和文档、和 config.json 一致。
            // 不加这一行会输出 PascalCase（Version / Menus / Label），虽然大小写不敏感
            // 仍然能读回来，但两份配置文件的风格不一致会让人以为它们是两种格式。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            // 不转义非 ASCII 字符：菜单里有中文标签，被转成 Unicode 转义序列之后
            // 手写编辑会非常难受（满屏六位码点，看不出哪个是哪个字）。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static MenuConfigMenu ToConfig(MenuSpec menu) => new()
    {
        Id = menu.Id,
        Name = menu.Name,
        Items = menu.Items.Select(ToConfigItem).ToList(),
    };

    private static MenuConfigItem ToConfigItem(MenuItemSpec item) => new()
    {
        Label = item.Label,
        Icon = item.IconPath,
        Angle = item.AngleDegrees,
        Action = item.Action is null ? null : new MenuConfigAction
        {
            Type = item.Action.Type,
            Target = item.Action.Target,
            Args = item.Action.Arguments,
            WorkingDirectory = item.Action.WorkingDirectory,
        },
        Children = item.Children.Count > 0 ? item.Children.Select(ToConfigItem).ToList() : null,
    };

    private string BuildDefaultMenusJson() => SerializeMenu(Hosting.DefaultMenu.Build());

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 渲染 config.json 的文本。
    ///
    /// 用"模板 + 插值"而不是直接序列化 <see cref="AppConfig"/>，是为了**保住注释**。
    /// 序列化会把注释全部丢掉，而这个文件是给人看的 —— 注释里写着每个字段的依据
    /// （比如悬停延迟为什么建议 200ms）。设置界面每次保存都会重写这个文件，
    /// 丢掉注释等于让用户失去唯一的说明来源。
    ///
    /// 代价：用户自己在 config.json 里加的注释会在设置界面保存后被覆盖。
    /// menus.json 不受影响 —— 设置界面不碰它。
    /// </summary>
    public static string RenderConfig(AppConfig config)
    {
        static string Str(string? value) => value is null ? "null" : $"\"{value}\"";
        static string Dec(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "null";
        static string Int(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
        static string Bool(bool? value) => value is null ? "null" : (value.Value ? "true" : "false");

        return $$"""
            {
              // WinQuick 用户配置。
              //
              // 这里只放"用户会调"的旋钮。视觉基线（配色、半径、时长、缓动曲线）在
              // design/tokens.json 里，属于开发者的设计决定。
              // 下面各项写成 null 表示「沿用设计基线」，只有显式设了值才覆盖。
              //
              // 改完保存即可生效，无需重启。
              // 写错了也不会导致程序打不开 —— 会保留上一份有效配置并在托盘提示。

              "version": 1,

              // 全局热键。格式：修饰键+主键，例如 "Ctrl+Win+W"、"Alt+Space"。
              // 注意 Win+单字母 在 Windows 11 上大多被系统预留（Win+W 是小组件），
              // 叠加一个 Ctrl 通常就能绕开。
              "hotkey": {{Str(config.Hotkey)}},

              // RegisterHotKey 失败时是否允许降级到低级键盘钩子抢键。
              // 关掉它意味着热键被占用时直接报错，而不会去抢键 —— 抢键会影响系统原有功能。
              "allowHookFallback": {{Bool(config.AllowHookFallback)}},

              "appearance": {
                // 全屏遮罩的不透明度，0–0.95。数值越大屏幕压得越暗。
                "dimOpacity": {{Dec(config.Appearance.DimOpacity)}},

                // 悬停高亮色，#RRGGBB。
                "accentColor": {{Str(config.Appearance.AccentColor)}}
              },

              "behavior": {
                // 悬停多久展开子菜单（毫秒）。
                // 实测依据：200ms 太快会误展开，250ms 时用户偏好五五开，400ms 更稳。
                // 设成 0 就是"立即展开" —— 允许，但指针划过时容易误触发。
                "hoverOpenDelayMs": {{Int(config.Behavior.HoverOpenDelayMs)}},

                // 离开多久收起子菜单（毫秒）。必须大于展开延迟，
                // 否则指针掠过扇区边缘时子菜单会闪。
                "hoverCloseDelayMs": {{Int(config.Behavior.HoverCloseDelayMs)}},

                // 是否播放动画。null = 跟随系统「在 Windows 中显示动画」的无障碍设置。
                "animationsEnabled": {{Bool(config.Behavior.AnimationsEnabled)}},

                // 覆盖层失去焦点时是否关闭。
                "hideOnFocusLoss": {{Bool(config.Behavior.HideOnFocusLoss)}}
              },

              "startup": {
                // 是否开机自启。改这里不会立刻写入注册表 ——
                // 自启开关以托盘菜单和设置界面里的那个为准，避免两处状态不一致。
                "autoStart": {{Bool(config.Startup.AutoStart)}}
              },

              // 使用哪一份菜单（menus.json 里的 id）。null = 用第一份。
              "activeMenu": {{Str(config.ActiveMenu)}}
            }
            """;
    }

    /// <summary>首次运行写出的那份配置内容（全部沿用设计基线）。</summary>
    public static string DefaultConfigJson => RenderConfig(new AppConfig());

    /// <summary>把配置写回文件。设置界面保存时调用。</summary>
    public bool SaveConfig(AppConfig config, out string? error)
    {
        error = null;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.WriteAllText(ConfigPath, RenderConfig(config), Utf8NoBom);

            var result = Reload();
            if (!result.AllOk) error = result.Summary;
            return result.ConfigOk;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            CrashLog.Write("保存配置失败", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounce.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileTouched;
            _watcher.Created -= OnFileTouched;
            _watcher.Renamed -= OnFileTouched;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
