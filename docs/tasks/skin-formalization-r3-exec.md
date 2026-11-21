# 执行书 R3：跟随系统动态皮肤 + --skin-probe

## T1 新建 src/WinQuick.App/Design/SystemThemeWatcher.cs（全文）
```csharp
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using Windows.Win32;

namespace WinQuick.App.Design;

/// <summary>
/// 系统主题监听（spec skin-formalization §5）：明暗模式 + 强调色。
///
/// 读注册表而不是 WinRT UISettings：WPF 直调 WinRT 要引入 CsWinRT 投影，
/// 而这两个值注册表就是权威真源（设置APP 写的就是它们）；
/// WM_SETTINGCHANGE(lParam="ImmersiveColorSet") 是系统切换主题时的标准广播。
/// 消息窗口范式照抄 SessionWatcher（同类需求同一写法，排查时心智一份）。
/// </summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    private readonly HwndSource _messageWindow;
    private bool _disposed;

    public SystemThemeWatcher()
    {
        var parameters = new HwndSourceParameters("WinQuick.SystemTheme")
        {
            Width = 1, Height = 1,
            PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP),
            ExtendedWindowStyle = 0,
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
    }

    /// <summary>系统明暗或强调色变化。UI 线程消息泵内触发。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>当前是否浅色应用模式（AppsUseLightTheme=1）。读不到按深色（本项目基线即深色）。</summary>
    public static bool IsLightMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }

    /// <summary>
    /// 系统强调色（#RRGGBB）。AccentColor 是 0xAABBGGRR 的 DWORD；
    /// 读不到时按 Fluent 官方默认（浅色 #0078D4 / 深色 #4CC2FF）。
    /// </summary>
    public static string AccentColor()
    {
        var fallback = IsLightMode() ? "#0078D4" : "#4CC2FF";
        using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
        if (key?.GetValue("AccentColor") is not int argb) return fallback;
        var r = argb & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = (argb >> 16) & 0xFF;
        // 过黑过白都不是可用的强调色（透明度残留或异常值），退回官方默认。
        if (r + g + b < 0x18 || r + g + b > 0x2E0) return fallback;
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE &&
            Marshal.PtrToStringUni(lParam) is "ImmersiveColorSet")
        {
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messageWindow.Dispose();
    }
}
```

## T2 新建 src/WinQuick.App/Design/SystemSkin.cs（全文）
```csharp
namespace WinQuick.App.Design;

/// <summary>
/// 「跟随系统」皮肤的运行时合成（spec §5）：不落 JSON 文件 —— 系统明暗是运行时事实，
/// 写死在 skins.json 就成了第二份真源。键集合参照 light 皮肤（明）与默认基线（暗），
/// 强调色映射到 hoverFill（Fluent：强调色用于交互态）。
/// </summary>
public static class SystemSkin
{
    public const string Id = "system";

    public static Core.Design.SkinSpec Spec() => new()
    {
        Id = Id,
        Name = "跟随系统",
        Tagline = "明暗与强调色跟 Windows 走（动态）",
        Cost = "运行时合成，无 JSON",
        Src = "出处：Win11 官方「应用默认跟随用户主题与强调色」（learn.microsoft.com/windows/apps/develop/ui/theming）",
        Overrides = null, // 动态合成见 Compose，静态目录里只挂名
    };

    /// <summary>按当前系统状态合成 overrides（与 light/暗基线同一键集，accent 映射 hoverFill）。</summary>
    public static System.Text.Json.Nodes.JsonObject Compose(bool light, string accentHex)
    {
        if (!light)
        {
            // 暗色=基线本身 + 强调色
            return new System.Text.Json.Nodes.JsonObject
            {
                ["color"] = new System.Text.Json.Nodes.JsonObject { ["hoverFill"] = accentHex },
            };
        }

        var accent = accentHex;
        return new System.Text.Json.Nodes.JsonObject
        {
            ["color"] = new System.Text.Json.Nodes.JsonObject
            {
                ["dimAlpha"] = 0.28,
                ["sectorFill"] = "#F3F3F3",
                ["sectorFillAlpha"] = 0.94,
                ["sectorStroke"] = "#000000",
                ["sectorStrokeAlpha"] = 0.08,
                ["hoverFill"] = accent,
                ["hoverFillAlpha"] = 1.0,
                ["hoverStroke"] = "#FFFFFF",
                ["hoverStrokeAlpha"] = 0.9,
                ["hoverLabel"] = "#FFFFFF",
                ["deadZoneFill"] = "#ECECEC",
                ["deadZoneStrokeAlpha"] = 0.16,
                ["label"] = "#1B1B1B",
                ["hint"] = "#1B1B1B",
                ["hintAlpha"] = 0.6,
                ["badgeFill"] = "#FFFFFF",
                ["badgeFillAlpha"] = 0.45,
            },
        };
    }
}
```

## T3 TokenStore.cs 接线（三处小改，先 Read 定位）
1. `ApplySkin` 开头 skinId 判空前加：
```csharp
        if (skinId == SystemSkin.Id) skinId = SkinCatalog.DefaultId; // system 无静态目录项，见 Load 特判
```
并删去上面新加行、改为在 `Load(skinId!)` 调用处特判：将 `var next = Load(skinId);` 改为：
```csharp
        var next = skinId == SystemSkin.Id
            ? LoadWithOverrides(SystemSkin.Compose(SystemThemeWatcher.IsLightMode(), SystemThemeWatcher.AccentColor()))
            : Load(skinId);
        if (skinId == SystemSkin.Id) CurrentSkinId = SystemSkin.Id;
```
（CurrentSkinId 原行 `CurrentSkinId = Skins.Contains(skinId) ? skinId! : SkinCatalog.DefaultId;` 保留——上面补行在其后修正 system；`LoadWithOverrides` 为新增 internal 静态：把 Load 中「拿 spec.Overrides 合并」的那半段抽出来接受 JsonObject——先 Read Load 全文，以最小改动抽出，Load 原语义不变。）
2. `PreviewTokens(skinId)` 同款特判：`if (skinId == SystemSkin.Id) return LoadWithOverrides(SystemSkin.Compose(...));`（先 Read 定位）。
3. `Skins.Contains(skinId)` 对 "system" 需返回 true——在 SkinCatalog.Contains（Core）加：`public bool Contains(string? id) => id == App 侧不可引用……` **不可**（Core 不引 App）——改为 Contains 保持不动，把调用侧（ApplySkin 首段校验）特判：`if (!string.IsNullOrWhiteSpace(skinId) && skinId != SystemSkin.Id && !Skins.Contains(skinId))`。
4. SkinPanel 列表追加第八项：先 Read SkinPanel.xaml.cs 的 `foreach (var skin in _catalog.All)`（约 85 行），循环体内对 skin 的用法 Read 后，在 foreach 之前构造 `var items = _catalog.All.Append(SystemSkin.Spec()).ToList();` 并把循环源改为 items（其余不动；行点击回调里的 skinId 链路自然支持 "system"）。SkinPanel 切换皮肤调用链（SkinChanged 事件→ResidentHost.OnSkinChanged→ApplySkin("system")）无需改。

## T4 ResidentHost.cs 接线监听
构造区（`_session = new SessionWatcher();` 附近）加：
```csharp
        _systemTheme = new Design.SystemThemeWatcher();
        // 「跟随系统」皮肤在系统切明暗/强调色时即时重应用（spec §5）。
        // 只有当前皮肤就是 system 时才动 —— 别的皮肤不该被系统主题打断。
        _systemTheme.ThemeChanged += (_, _) =>
        {
            if (Design.TokenStore.CurrentSkinId == Design.SystemSkin.Id)
                RunOnUi(() => { Design.TokenStore.ApplySkin(Design.SystemSkin.Id); _overlay.RebuildForAppearance(_menu is null ? Radial.RingForm.Pie : CurrentRingForm); });
        };
```
（CurrentRingForm 不存在——改为实际取当前形态：`_config.Config.Appearance.Form` 经 RingForms.Parse；字段声明 `private readonly Design.SystemThemeWatcher _systemTheme;` 加在 _session 附近；RebuildForAppearance 的参数以 OnSkinChanged 里现有写法为准照抄——先 Read OnSkinChanged。Dispose 处（OnExit/Dispose 模式）补 `_systemTheme.Dispose();`。）

## T5 --skin-probe 探针
新建 src/WinQuick.Probes/M0/SkinProbe.cs（照 SettingsProbe/ChildArcPerfProbe 骨架，RunAsync 模式）：
- 起临时 OverlayController（参照 ChildArcPerfProbe）；
- 用例1 换肤后资源翻转：记录 `Application.Current.Resources["Wq.Color.SurfaceSolid"]`（Install 前后对比 Brush.ToString 不同）→ default vs light 两态断言不同；
- 用例2 浅色派生键翻转：light 下 `Wq.Color.HoverSurface` 与 dark 下不同；
- 用例3 system 合成：PreviewTokens("system") 在伪装明/暗两态（直接调 SystemSkin.Compose 两种参数+LoadWithOverrides）Validate 无问题；
- 对照：default→default 资源不变（防恒真）。
ProbesHost 注册（--skin-probe）+帮助行（照 R5 模式）。
（可访问性：LoadWithOverrides internal + InternalsVisibleTo ✔；探针读 Application.Current.Resources 需 UI 线程——RunDetached 模式满足。）

## 验证
1. dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo → 0 error；
2. dotnet build src/WinQuick.Probes/WinQuick.Probes.csproj -c Release --nologo → 0 error；
3. dotnet test tests/WinQuick.Core.Tests --nologo → 全量不回退。