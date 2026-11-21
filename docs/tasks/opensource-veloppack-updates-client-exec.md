---
slug: opensource-veloppack-updates-client-exec
task: docs/tasks/opensource-veloppack-updates-client.md
spec: docs/specs/opensource-veloppack-updates.md
date: 2026-11-21
---

# 执行书 A：客户端 Velopack 集成（spec 阶段一）

## 第 0.5 步：安装包引用

在 D:\Work\WinQuick\src\WinQuick.App 目录执行：`dotnet add package Velopack`（取最新稳定版，自动写入 csproj）。

## 第 1 步：机械应用以下补丁（每处先 Read 定向行段核对 old，逐字符匹配后替换）

### P1a WinQuick.App.csproj
old:
```
    <AssemblyName>WinQuick</AssemblyName>
```
new:
```
    <AssemblyName>WinQuick</AssemblyName>
    <!-- Velopack 要求 semver2 三段版本（不支持 1.0.0.0 四段），assembly 版本随之走。 -->
    <Version>1.1.0</Version>
    <!-- 自定义入口（Program.cs）：Velopack 引导必须先于任何 WPF 初始化。 -->
    <StartupObject>WinQuick.App.Program</StartupObject>
```

### P1b WinQuick.App.csproj
old:
```
  <ItemGroup>
    <ProjectReference Include="..\WinQuick.Core\WinQuick.Core.csproj" />
  </ItemGroup>
```
new:
```
  <ItemGroup>
    <ProjectReference Include="..\WinQuick.Core\WinQuick.Core.csproj" />
  </ItemGroup>

  <!-- App.xaml 不再作为 ApplicationDefinition（入口由 Program.cs 接管），
       改按普通 Page 编译 —— 资源与 x:Class 生成物不变。 -->
  <ItemGroup>
    <ApplicationDefinition Remove="App.xaml" />
    <Page Include="App.xaml" />
  </ItemGroup>
```

### P2 新建 src/WinQuick.App/Program.cs（全文）
```csharp
using System;

namespace WinQuick.App;

/// <summary>
/// 自定义入口。Velopack 的引导必须发生在任何 WPF 初始化之前 ——
/// 更新安装后的首次启动由它处理完安装钩子再放行（spec §3.4）。
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
```

### P3 新建 src/WinQuick.App/Updates/UpdateChecker.cs（全文）
```csharp
using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace WinQuick.App.Updates;

/// <summary>
/// Velopack 更新检查。所有网络失败一律静默降级 —— 更新是增强不是主功能，
/// 断网/限流/解析失败不该打扰任何路径（spec §3.4）。
/// 仓库固定为 github.com/FM1503/WinQuick，仅正式版（prerelease: false）。
/// </summary>
public sealed class UpdateChecker
{
    private const string RepoUrl = "https://github.com/FM1503/WinQuick";

    private readonly UpdateManager? _manager;

    public UpdateChecker()
    {
        // 开发目录直接运行（未经安装器）时没有本地版本元数据，UpdateManager
        // 构造/检查会失败 —— 捕获后 IsSupported=false，调用方一律按无更新处理。
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, prerelease: false));
        }
        catch
        {
            _manager = null;
        }
    }

    /// <summary>当前安装方式是否支持在线更新（dotnet run / 旧 publish 目录不支持）。</summary>
    public bool IsSupported => _manager is { IsInstalled: true };

    /// <summary>最近一次检查命中的更新目标；DownloadAsync/ApplyAndRestart 以它为对象。</summary>
    public UpdateInfo? CachedTarget { get; private set; }

    /// <summary>检查新版。无更新/不可用/失败都返回 null（不抛出）。</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!IsSupported || _manager is null) return null;
        try
        {
            CachedTarget = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return CachedTarget;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>下载到 Velopack 本地缓存（增量优先）。失败返回 false。</summary>
    public async Task<bool> DownloadAsync(UpdateInfo target)
    {
        if (!IsSupported || _manager is null) return false;
        try
        {
            await _manager.DownloadUpdatesAsync(target).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>应用更新并重启。只应被用户显式动作触发（spec：绝不自动重启）。</summary>
    public void ApplyAndRestart(UpdateInfo target)
    {
        if (!IsSupported || _manager is null) return;
        _manager.ApplyUpdatesAndRestart(target);
    }

    /// <summary>当前运行版本（三段）；开发运行解析不出时为 null。</summary>
    public static string? CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
}
```

### P4a src/WinQuick.Core/Config/AppConfig.cs
old:
```
    [JsonPropertyName("startup")] public StartupConfig Startup { get; init; } = new();
```
new:
```
    [JsonPropertyName("startup")] public StartupConfig Startup { get; init; } = new();

    [JsonPropertyName("updates")] public UpdateConfig Updates { get; init; } = new();
```

### P4b src/WinQuick.Core/Config/AppConfig.cs
old:
```
public sealed record StartupConfig
{
    /// <summary>是否开机自启。首次运行会按它写入 HKCU Run。</summary>
    public bool AutoStart { get; init; }
}
```
new:
```
public sealed record StartupConfig
{
    /// <summary>是否开机自启。首次运行会按它写入 HKCU Run。</summary>
    public bool AutoStart { get; init; }
}

public sealed record UpdateConfig
{
    /// <summary>启动后是否静默检查 GitHub Releases 更新（spec §3.7）。默认开。</summary>
    public bool AutoCheck { get; init; } = true;
}
```

### P5 src/WinQuick.App/Config/ConfigStore.cs
old:
```
              "startup": {
                // 是否开机自启。改这里不会立刻写入注册表 ——
                // 自启开关以托盘菜单和设置界面里的那个为准，避免两处状态不一致。
                "autoStart": {{Bool(config.Startup.AutoStart)}}
              },

              // 使用哪一份菜单（menus.json 里的 id）。null = 用第一份。
              "activeMenu": {{Str(config.ActiveMenu)}}
            }
```
new:
```
              "startup": {
                // 是否开机自启。改这里不会立刻写入注册表 ——
                // 自启开关以托盘菜单和设置界面里的那个为准，避免两处状态不一致。
                "autoStart": {{Bool(config.Startup.AutoStart)}}
              },

              "updates": {
                // 启动后是否静默检查 GitHub Releases 更新（失败静默，绝不自动重启）。
                "autoCheck": {{Bool(config.Updates.AutoCheck)}}
              },

              // 使用哪一份菜单（menus.json 里的 id）。null = 用第一份。
              "activeMenu": {{Str(config.ActiveMenu)}}
            }
```

### P6 src/WinQuick.App/Hosting/SingleInstance.cs
old:
```
    public static bool SetEnabled(bool enabled) => enabled ? Enable() : Disable();
```
new:
```
    /// <summary>
    /// 自启项路径与当前 exe 不一致时改写为当前路径。
    /// 场景：从旧 publish 目录迁到 Velopack 安装目录 —— 自启只在勾选时写一次，
    /// Run 项还指着旧位置。按「用户意图已启用」处理，只纠正位置，不改变开关状态。
    /// </summary>
    public static void RepairStalePath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string entry) return;
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return;
            if (string.Equals(entry.Trim('"'), path, StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
        }
        catch
        {
            // 注册表此刻不可写就算了 —— 下次启动还会再试。
        }
    }

    public static bool SetEnabled(bool enabled) => enabled ? Enable() : Disable();
```

### P7a src/WinQuick.App/Hosting/ResidentHost.cs（字段区，先 Read 定位 `private readonly TrayHost _tray;`）
old:
```
    private readonly TrayHost _tray;
```
new:
```
    private readonly TrayHost _tray;

    /// <summary>更新检查器。「关于与更新」分区与启动静默检查共用一份（含已下载状态）。</summary>
    private readonly Updates.UpdateChecker _updater = new();
```

### P7b ResidentHost.cs（构造装配区）
old:
```
        ActionExecutor.Completed += OnActionCompleted;
```
new:
```
        // 「关于与更新」分区的检查与重启：面板不认识 UpdateChecker（宿主的职责），
        // 经 OverlayController 转发接缝注入 —— 与 SetHotkeyProbe 同一套模式。
        // 检查返回给面板的是一句状态文案；重启只在下载就绪后由用户显式点击。
        _overlay.SetUpdateHooks(
            async () =>
            {
                var info = await _updater.CheckAsync();
                if (info is null)
                    return _updater.IsSupported ? "已是最新版本" : "（开发目录运行，不支持在线更新）";
                var ok = await _updater.DownloadAsync(info);
                return ok
                    ? $"新版本 {info.TargetFullRelease.Version} 已就绪，可立即更新"
                    : $"发现新版本 {info.TargetFullRelease.Version}，下载失败（稍后再试）";
            },
            () =>
            {
                if (_updater.CachedTarget is { } target) _updater.ApplyAndRestart(target);
            });

        ActionExecutor.Completed += OnActionCompleted;
```

### P7c ResidentHost.cs（Start 内自启种子行后）
old:
```
        if (_config.DefaultsWereCreated) AutoStart.SetEnabled(_config.Config.Startup.AutoStart);
```
new:
```
        if (_config.DefaultsWereCreated) AutoStart.SetEnabled(_config.Config.Startup.AutoStart);

        // 自启路径自愈：从旧 publish 目录迁到 Velopack 安装目录后，Run 项仍指旧位置。
        AutoStart.RepairStalePath();
```

### P7d ResidentHost.cs（Start 尾部）
old:
```
        _config.StartWatching();
        _config.Changed += OnConfigChanged;
```
new:
```
        _config.StartWatching();
        _config.Changed += OnConfigChanged;

        // 启动静默检查更新：延迟 15 秒避开开机高负载（与启动队列同一考量）。
        // 检查/下载全程静默，仅下载完成时气泡提示；重启永远由用户显式触发。
        if (_config.Config.Updates.AutoCheck)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                RunOnUi(() => _ = CheckForUpdatesQuietly());
            });
        }
```

### P7e ResidentHost.cs（CheckForUpdatesQuietly 方法，插在 OnSettingsChanged 方法之前；先 Read 定位 `private void OnSettingsChanged(AppConfig config)`）
old:
```
    private void OnSettingsChanged(AppConfig config)
```
new:
```
    /// <summary>启动后的静默更新检查。任何失败都无声跳过（spec §3.4：更新是增强不是主功能）。</summary>
    private async System.Threading.Tasks.Task CheckForUpdatesQuietly()
    {
        try
        {
            var info = await _updater.CheckAsync();
            if (info is null) return;
            var ok = await _updater.DownloadAsync(info);
            if (ok)
                _tray.ShowBalloon("WinQuick 更新",
                    $"新版本 {info.TargetFullRelease.Version} 已就绪，点此打开「关于」重启更新",
                    onClick: () => OpenSettings(jumpToAbout: true));
        }
        catch
        {
            // 静默：断网/限流/解析失败都不打扰。
        }
    }

    private void OnSettingsChanged(AppConfig config)
```

### P7f ResidentHost.cs（OpenSettings 签名）
old:
```
    public void OpenSettings()
    {
        _overlay.SetSettingsSource(_config.Config);
```
new:
```
    public void OpenSettings(bool jumpToAbout = false)
    {
        _overlay.SetSettingsSource(_config.Config);
```
并将该方法体内 `_overlay.OpenSettings();` 改为 `_overlay.OpenSettings(jumpToAbout);`（该行在 `if (!_overlay.IsVisible) _overlay.Show();` 之后）。

### P8a src/WinQuick.App/Hosting/OverlayController.cs（字段区）
old:
```
    private MenuSpec _menuSource;
```
new:
```
    private MenuSpec _menuSource;

    /// <summary>「关于与更新」分区接缝（ResidentHost 注入，面板创建时转交）。</summary>
    private Func<Task<string>>? _updateCheck;
    private Action? _updateRestart;
```

### P8b OverlayController.cs（SetHotkeyProbe 之后）
old:
```
    /// <summary>注入热键探测函数。</summary>
    public void SetHotkeyProbe(Func<Core.Hotkey.HotkeySpec, (bool Available, int ErrorCode)> probe) =>
        _settingsPanelProbe = spec => probe(spec);
```
new:
```
    /// <summary>注入热键探测函数。</summary>
    public void SetHotkeyProbe(Func<Core.Hotkey.HotkeySpec, (bool Available, int ErrorCode)> probe) =>
        _settingsPanelProbe = spec => probe(spec);

    /// <summary>注入「关于与更新」分区的检查与重启接缝。</summary>
    public void SetUpdateHooks(Func<Task<string>> check, Action restart)
    {
        _updateCheck = check;
        _updateRestart = restart;
        if (_settingsPanel is { } panel) panel.SetUpdateHooks(check, restart);
    }
```

### P8c OverlayController.cs（CreateSettingsPanel 绑定区）
old:
```
        panel.AddQueueItemRequested = () => PickMenuItemForAdd(forQueue: true);
```
new:
```
        panel.AddQueueItemRequested = () => PickMenuItemForAdd(forQueue: true);

        // 更新接缝可能先于面板设置：创建时把已有的立刻交过去（模式同上）。
        if (_updateCheck is not null) panel.SetUpdateHooks(_updateCheck, _updateRestart!);
```

### P8d OverlayController.cs（OpenSettings 签名）
old:
```
    public void OpenSettings()
    {
        if (_disposed || !IsVisible) return;
```
new:
```
    public void OpenSettings(bool jumpToAbout = false)
    {
        if (_disposed || !IsVisible) return;
```

### P8e OverlayController.cs（OpenSettings 尾部，ForegroundFocus.AttachTo(_settingsPanel); 唯一）
old:
```
        ForegroundFocus.AttachTo(_settingsPanel);
    }
```
new:
```
        ForegroundFocus.AttachTo(_settingsPanel);

        // 气泡引导进来时直达「关于与更新」，省一次侧栏寻找。
        if (jumpToAbout) _settingsPanel.SelectAboutPage();
    }
```

### P9 src/WinQuick.App/Tray/TrayHost.cs（ShowBalloon 扩展点击回调）
old:
```
    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.ShowNotification(title, message);
        }
```
new:
```
    public void ShowBalloon(string title, string message, Action? onClick = null)
    {
        try
        {
            if (onClick is not null)
            {
                // 重挂而不是叠加：上一次气泡的点击回调不该串到这一次。
                _icon.TrayBalloonTipClicked -= OnBalloonClicked;
                OnBalloonClicked = (_, _) => onClick();
                _icon.TrayBalloonTipClicked += OnBalloonClicked;
            }
            _icon.ShowNotification(title, message);
        }
```
并在字段区（`private bool _disposed;` 之前或之后）加：
```csharp
    /// <summary>气泡点击回调的当前挂载实例（重挂用，见 ShowBalloon）。</summary>
    private EventHandler? OnBalloonClicked;
```

### P10a src/WinQuick.App/Radial/SettingsPanel.xaml（侧栏末项）
old:
```
                    <ListBoxItem>启动</ListBoxItem>
                </ListBox>
```
new:
```
                    <ListBoxItem>启动</ListBoxItem>
                    <ListBoxItem>关于</ListBoxItem>
                </ListBox>
```

### P10b SettingsPanel.xaml（PageAbout 容器，插在启动队列注释之前）
old:
```
                        <!-- ============ 启动队列 ============
```
new:
```
                        <!-- ============ 关于与更新 ============
                             版式沿用 GroupCard 分区；状态行文案由宿主注入的检查接缝给出。 -->
                        <StackPanel x:Name="PageAbout" Visibility="Collapsed">
                            <Border Style="{StaticResource GroupCard}" Margin="0,0,0,0">
                                <StackPanel Margin="14,10,14,12">
                                    <TextBlock Style="{StaticResource CardTitle}" Text="WinQuick" />
                                    <TextBlock x:Name="AboutVersion" Style="{StaticResource CardDesc}"
                                               Text="版本（未知）" />
                                    <TextBlock x:Name="UpdateStatus" Style="{StaticResource Hint}"
                                               Margin="0,10,0,0" Text="（尚未检查）" />
                                    <StackPanel Orientation="Horizontal" Margin="0,12,0,0">
                                        <Button x:Name="CheckUpdateButton" Style="{StaticResource FlatButton}"
                                                Content="检查更新" Click="OnCheckUpdateClick" />
                                        <Button x:Name="RestartUpdateButton" Style="{StaticResource FlatButton}"
                                                Margin="10,0,0,0" Visibility="Collapsed"
                                                Content="立即更新并重启" Click="OnRestartUpdateClick" />
                                    </StackPanel>
                                    <CheckBox x:Name="AutoUpdateBox" Margin="0,12,0,0"
                                              Content="启动时自动检查更新" Click="OnAutoUpdateChanged" />
                                </StackPanel>
                            </Border>
                        </StackPanel>

                        <!-- ============ 启动队列 ============
```
（注意：该注释锚点若在文件中多处出现，以 PageStartup 容器结束后的那处为准——先用 Read 定向核对 525-535 行。）

### P11a SettingsPanel.xaml.cs（pages 数组）
old:
```
        var pages = new UIElement[] { PageHotkey, PageLook, PageBehavior, PageMenu, PageQueues, PageStartup };
```
new:
```
        var pages = new UIElement[] { PageHotkey, PageLook, PageBehavior, PageMenu, PageQueues, PageStartup, PageAbout };
```

### P11b SettingsPanel.xaml.cs（LoadFrom 装载）
old:
```
        // 自启的事实来源是注册表（托盘菜单也能改），所以读注册表而不是配置。
        AutoStartBox.IsChecked = AutoStart.IsEnabled;
```
new:
```
        // 自启的事实来源是注册表（托盘菜单也能改），所以读注册表而不是配置。
        AutoStartBox.IsChecked = AutoStart.IsEnabled;

        AutoUpdateBox.IsChecked = config.Updates.AutoCheck;
```

### P11c SettingsPanel.xaml.cs（接缝字段区，先 Read 定位 `private Func<IReadOnlyList<LaunchQueueSpec>, bool>? _queueSave;`）
old:
```
    private Func<IReadOnlyList<LaunchQueueSpec>, bool>? _queueSave;
```
new:
```
    private Func<IReadOnlyList<LaunchQueueSpec>, bool>? _queueSave;

    /// <summary>「关于与更新」分区接缝：check 返回状态文案（含「已就绪」时显示重启按钮）。</summary>
    private Func<Task<string>>? _updateCheck;
    private Action? _updateRestart;
    private bool _updateReady;

    internal void SetUpdateHooks(Func<Task<string>>? check, Action? restart)
    {
        _updateCheck = check;
        _updateRestart = restart;
    }

    /// <summary>气泡引导进入时直达本分区（侧栏最后一项）。</summary>
    public void SelectAboutPage() => NavList.SelectedIndex = NavList.Items.Count - 1;
```

### P11d SettingsPanel.xaml.cs（构造器尾部）
old:
```
        LoadFrom(current, notify: false);
    }
```
new:
```
        LoadFrom(current, notify: false);

        AboutVersion.Text = $"版本 {Updates.UpdateChecker.CurrentVersion ?? "（开发运行）"}";
    }
```
（若该 old 在文件中出现多处，以构造函数内那处为准——Read 定向核对 90-96 行。）

### P11e SettingsPanel.xaml.cs（新 handler，插在 OnAutoStartChanged 方法结束之后；先 Read 定位该方法）
在 `OnAutoStartChanged` 方法体结束后、`OnOpenFolderClick` 之前插入：
```csharp

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyAndPersist();
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateCheck is null)
        {
            UpdateStatus.Text = "（更新组件不可用）";
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "正在检查…";
        try
        {
            var status = await _updateCheck();
            UpdateStatus.Text = $"{status}（{DateTime.Now:HH:mm}）";
            _updateReady = status.Contains("已就绪");
            RestartUpdateButton.Visibility = _updateReady
                ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnRestartUpdateClick(object sender, RoutedEventArgs e)
    {
        // 重启由用户显式点击触发（spec §3.4：绝不自动重启）。
        if (_updateReady) _updateRestart?.Invoke();
    }
```

### P11f SettingsPanel.xaml.cs（BuildConfig）
old:
```
            Startup = new StartupConfig { AutoStart = AutoStartBox.IsChecked == true },
```
new:
```
            Startup = new StartupConfig { AutoStart = AutoStartBox.IsChecked == true },
            Updates = new UpdateConfig { AutoCheck = AutoUpdateBox.IsChecked == true },
```

### P12 新建 tests/WinQuick.Core.Tests/UpdateConfigTests.cs（全文）
```csharp
using WinQuick.App.Config;
using WinQuick.Core.Config;

namespace WinQuick.Core.Tests;

/// <summary>
/// updates 配置段的三条底线：旧配置缺段=默认开（升级不丢静默检查）、显式关闭能往返、
/// 渲染模板必须含该段（漏段=保存即重置 —— 皮肤字段踩过的坑，见 docs/changes/fix-skin-form-persistence.md）。
/// </summary>
public class UpdateConfigTests
{
    [Fact]
    public void 旧配置缺段_默认开启静默检查()
    {
        var parsed = AppConfig.Parse("""{ "version": 1 }""");
        Assert.True(parsed.Updates.AutoCheck);
    }

    [Fact]
    public void 显式关闭_渲染解析往返保持()
    {
        var config = new AppConfig { Updates = new UpdateConfig { AutoCheck = false } };
        var round = AppConfig.Parse(ConfigStore.RenderConfig(config));
        Assert.False(round.Updates.AutoCheck);
    }

    [Fact]
    public void 渲染模板含_updates段_且默认渲染往返为开()
    {
        var round = AppConfig.Parse(ConfigStore.RenderConfig(new AppConfig()));
        Assert.True(round.Updates.AutoCheck);
    }
}
```

## 第 2 步：验证（贴回输出）

1. `dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo` → 预期 0 error（0 warning 或与许可条款对应）；
2. `dotnet test tests/WinQuick.Core.Tests --filter UpdateConfig --nologo` → 预期 3 用例全绿；
3. `git diff --stat` → 预期仅本任务书 13 个文件 + csproj 的 PackageReference 行变化。
