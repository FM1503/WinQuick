using System.Windows;
using WinQuick.App.Config;
using WinQuick.App.Design;
using WinQuick.App.Diagnostics;
using WinQuick.App.Hosting;
using WinQuick.App.Radial;

namespace WinQuick.App.M0;

/// <summary>
/// 换肤链路探针（spec skin-formalization §5 / R3）。
///
/// 观感归用户，这里只核对**可判定的接线**：
/// 1. 换肤真的会翻转 Application.Current 里的资源（default vs light 的 SurfaceSolid 不同）；
/// 2. 浅色派生键（HoverSurface）跟着翻转 —— 只翻基色不翻派生键＝R1 的接线断了；
/// 3. system 皮肤两态合成（伪装明/暗参数）通过同一套 Validate，且 ApplySkin("system")
///    被识别、CurrentSkinId 落定 system；
/// 对照：default→default 资源不变（防恒真 —— 上面几条不能靠"每次都不同"蒙过）。
///
/// 用法：--skin-probe
/// </summary>
public static class SkinProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("skin", "m4");
        var results = new List<(string Name, bool Passed, string Detail)>();
        OverlayController? overlay = null;

        try
        {
            // 探针进程不走常驻装配，资源要自己装一次（ApplySkin 内部的 Install 依赖它建立基线）。
            TokenResources.Install(force: true);

            var menu = new ConfigStore(ConfigStore.DefaultDirectory).Menu;
            overlay = new OverlayController(menu);
            overlay.Show();
            await Task.Delay(300);

            var darkSurface = Res("Wq.Color.SurfaceSolid");
            var darkHover = Res("Wq.Color.HoverSurface");

            ProbeOutput.Section("换肤后资源翻转");
            var problemLight = TokenStore.ApplySkin("light");
            overlay.RebuildForAppearance(RingForm.Pie);
            await Task.Delay(200);
            var lightSurface = Res("Wq.Color.SurfaceSolid");
            var lightHover = Res("Wq.Color.HoverSurface");
            results.Add(("default→light：SurfaceSolid 翻转",
                problemLight is null && darkSurface != lightSurface,
                $"dark={darkSurface} → light={lightSurface}（problem={problemLight ?? "null"}）"));
            results.Add(("default→light：派生键 HoverSurface 翻转",
                darkHover != lightHover,
                $"dark={darkHover} → light={lightHover}"));

            ProbeOutput.Section("system 合成（伪装明/暗两态）");
            var darkSys = TokenStore.LoadWithOverrides(SystemSkin.Compose(false, "#4CC2FF"));
            var lightSys = TokenStore.LoadWithOverrides(SystemSkin.Compose(true, "#0078D4"));
            var darkProblems = darkSys.Validate();
            var lightProblems = lightSys.Validate();
            results.Add(("system 暗态：Validate 无问题", darkProblems.Count == 0,
                darkProblems.Count == 0 ? "" : string.Join("; ", darkProblems)));
            results.Add(("system 亮态：Validate 无问题", lightProblems.Count == 0,
                lightProblems.Count == 0 ? "" : string.Join("; ", lightProblems)));
            results.Add(("system 亮态：强调色落到 hoverFill",
                lightSys.Color.HoverFill == "#0078D4",
                $"hoverFill={lightSys.Color.HoverFill}"));
            var liveSys = TokenStore.PreviewTokens(SystemSkin.Id);
            results.Add(("PreviewTokens(system) 按真实系统状态可算",
                liveSys.Validate().Count == 0, $"明暗={SystemThemeWatcher.IsLightMode()}"));

            var problemSys = TokenStore.ApplySkin(SystemSkin.Id);
            results.Add(("ApplySkin(system) 被识别且 CurrentSkinId 落定",
                problemSys is null && TokenStore.CurrentSkinId == SystemSkin.Id,
                $"problem={problemSys ?? "null"}，CurrentSkinId={TokenStore.CurrentSkinId}"));


            var glassTok = TokenStore.PreviewTokens("translucent");
            results.Add(("玻璃皮肤：scrim 保底层已装载（spec R2 D 档）",
                glassTok.Color.SectorScrimAlpha > 0.6 && glassTok.Color.SectorScrim == "#000000",
                $"scrim={glassTok.Color.SectorScrim}@{glassTok.Color.SectorScrimAlpha}"));
            results.Add(("玻璃皮肤：死区回填值不再纯白（缺陷回归锁）",
                glassTok.Color.DeadZoneFill != "#FFFFFF",
                $"deadZoneFill={glassTok.Color.DeadZoneFill}"));
            results.Add(("AccentText 资源键存在（选中行 on-accent 配对）",
                Res("Wq.Color.AccentText") != "<null>",
                Res("Wq.Color.AccentText")));
            ProbeOutput.Section("对照：default→default 资源不变");
            TokenStore.ApplySkin(null);
            var backDark = Res("Wq.Color.SurfaceSolid");
            TokenStore.ApplySkin(null);
            var againDark = Res("Wq.Color.SurfaceSolid");
            results.Add(("对照成立：两次 default 装配资源相同",
                backDark == againDark && backDark == darkSurface,
                $"{backDark} vs {againDark}（初始 {darkSurface}）"));
        }
        catch (Exception ex)
        {
            results.Add(("探针异常", false, ex.ToString()));
        }
        finally
        {
            overlay?.Hide();
            ProbeOutput.Section("结论");
            var allPass = results.All(r => r.Passed);
            foreach (var (name, passed, detail) in results)
                ProbeOutput.Kv(name, (passed ? "PASS" : "FAIL") + "  " + detail);
            ProbeOutput.Line(allPass ? "全部通过" : "存在失败项");
            ProbeOutput.Flush();
        }
    }

    private static string Res(string key) =>
        Application.Current.Resources[key]?.ToString() ?? "<null>";
}
