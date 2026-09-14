using System.IO;
using System.Windows.Interop;
using WinQuick.App.Config;
using WinQuick.App.Diagnostics;
using WinQuick.App.Settings;
using WinQuick.Core.Config;
using WinQuick.Core.Hotkey;

namespace WinQuick.App.M0;

/// <summary>
/// 设置界面探针。
///
/// 界面里"好不好看"只能靠人看，但有几件事是可以程序化核对的，而且恰恰是风险最高的：
/// 1. **XAML 能不能加载** —— 控件名与 code-behind 对不上会直接抛异常，
///    而这类错误只在真正打开窗口时才暴露。
/// 2. **保存路径**（从控件读值 → 组装模型 → 校验 → 落盘）—— 一堆约定在这里：
///    留空表示沿用设计基线、滑块值等于默认值时写回 null、非法输入要报错而不是静默丢弃。
/// 3. **热键实时测试**能否给出正确结论。
/// </summary>
public static class SettingsProbe
{
    public static async Task RunAsync(CliOptions cli)
    {
        ProbeOutput.Initialize("settings", "m4");

        var temp = Path.Combine(Path.GetTempPath(), "winquick-settings-probe-" + Environment.ProcessId);
        var results = new List<(string Name, bool Passed, string Detail)>();

        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);

            var store = new ConfigStore(temp);
            store.LoadOrCreateDefaults();

            ProbeOutput.Section("窗口能否加载");
            SettingsWindow? window = null;
            try
            {
                window = new SettingsWindow(
                    store.Config,
                    probeHotkey: spec =>
                    {
                        // 探针里不真的去抢键，直接用解析器判断组合是否合法，
                        // 再按已知的占用情况给结论 —— 这样验证的是"界面如何呈现结论"，
                        // 而不是重复验证热键注册本身（那由 --hook-probe 覆盖）。
                        var valid = HotkeySpec.TryParse(spec.ToString(), out _, out _);
                        return (valid, valid ? 0 : 87);
                    },
                    save: config =>
                    {
                        var ok = store.SaveConfig(config, out var error);
                        return (ok, error);
                    });

                ProbeOutput.Kv("构造成功（XAML 与 code-behind 匹配）", "是");
                results.Add(("XAML 加载", true, ""));
            }
            catch (Exception ex)
            {
                ProbeOutput.Kv("构造失败", ex.Message);
                results.Add(("XAML 加载", false, ex.Message));
                Report(results);
                return;
            }

            // 真正显示一次 —— 构造成功不代表能显示（布局异常、资源缺失等只在显示时暴露）。
            var shown = false;
            var handleCreated = false;
            try
            {
                window.Show();
                await Task.Delay(400);

                shown = window.IsVisible;
                handleCreated = new WindowInteropHelper(window).Handle != IntPtr.Zero;

                ProbeOutput.Kv("显示成功", shown ? "是" : "否");
                ProbeOutput.Kv("窗口句柄已创建", handleCreated ? "是" : "否");
                ProbeOutput.Kv("窗口尺寸", $"{window.ActualWidth:0} x {window.ActualHeight:0}");
            }
            catch (Exception ex)
            {
                ProbeOutput.Kv("显示失败", ex.Message);
            }
            finally
            {
                try { window.Close(); } catch { /* 已关闭则忽略 */ }
            }

            results.Add(("窗口可显示", shown && handleCreated, $"{window.ActualWidth:0}x{window.ActualHeight:0}"));

            // 关掉显示之后重建一个干净的来做保存路径验证 ——
            // 上面那个已经被 Close 了，控件状态不再可靠。
            window = new SettingsWindow(store.Config, _ => (true, 0),
                config => (store.SaveConfig(config, out var error), error));

            ProbeOutput.Section("保存路径");

            // ---- 正常保存 ----
            window.SetHotkeyForTest(new HotkeySpec(HotkeyModifiers.Alt, 0x20, "Space"));
            window.SetDelaysForTest(300, 500);
            window.SetDimOpacityForTest(0.6);
            window.SetAccentForTest("FF8800");
            window.SetAutoStartForTest(false);

            var (ok, message) = window.SaveCurrentSettings();
            ProbeOutput.Kv("保存结果", ok ? "成功" : $"失败：{message}");
            ProbeOutput.Kv("界面状态文本", window.SaveStatusTextForTest.Trim());

            var saved = AppConfig.Parse(File.ReadAllText(store.ConfigPath));
            ProbeOutput.Kv("热键已写入", saved.Hotkey ?? "(空)");
            ProbeOutput.Kv("展开/收起延迟已写入", $"{saved.Behavior.HoverOpenDelayMs} / {saved.Behavior.HoverCloseDelayMs}");
            ProbeOutput.Kv("遮罩不透明度已写入", saved.Appearance.DimOpacity?.ToString() ?? "(空)");
            ProbeOutput.Kv("高亮色已写入", saved.Appearance.AccentColor ?? "(空)");
            ProbeOutput.Kv("高亮色自动补了 #", saved.Appearance.AccentColor?.StartsWith('#') == true ? "是" : "否（异常）");

            var normalOk = ok
                && saved.Hotkey == "Alt+Space"
                && saved.Behavior.HoverOpenDelayMs == 300
                && saved.Behavior.HoverCloseDelayMs == 500
                && Math.Abs((saved.Appearance.DimOpacity ?? 0) - 0.6) < 0.001
                && saved.Appearance.AccentColor == "#FF8800";

            results.Add(("正常保存写入正确", normalOk, saved.Hotkey ?? ""));

            // ---- 注释必须保住 ----
            // 设置界面每次保存都会重写 config.json。如果用序列化而不是模板渲染，
            // 那些解释每个字段依据的注释会被抹掉。
            var text = File.ReadAllText(store.ConfigPath);
            var keepsComments = text.Contains("//") && text.Contains("设计基线");
            ProbeOutput.Kv("保存后注释仍在", keepsComments ? "是" : "否（注释被抹掉了）");
            results.Add(("保存后保住注释", keepsComments, ""));

            // ---- 留空 = 沿用设计基线 ----
            ProbeOutput.Section("留空表示沿用设计基线");
            window.SetDelaysForTest(null, null);
            window.SetDimOpacityForTest(0.4);   // 等于默认值
            window.SetAccentForTest("");
            window.SaveCurrentSettings();

            saved = AppConfig.Parse(File.ReadAllText(store.ConfigPath));
            ProbeOutput.Kv("延迟写回 null", saved.Behavior.HoverOpenDelayMs is null && saved.Behavior.HoverCloseDelayMs is null ? "是" : "否");
            ProbeOutput.Kv("遮罩值等于默认时写回 null", saved.Appearance.DimOpacity is null ? "是" : "否");
            ProbeOutput.Kv("高亮色留空写回 null", saved.Appearance.AccentColor is null ? "是" : "否");

            var baselineOk = saved.Behavior.HoverOpenDelayMs is null
                             && saved.Behavior.HoverCloseDelayMs is null
                             && saved.Appearance.DimOpacity is null
                             && saved.Appearance.AccentColor is null;

            results.Add(("留空写回 null", baselineOk, ""));
            ProbeOutput.Line();
            ProbeOutput.Line(baselineOk
                ? "  >>> 留空即沿用设计基线：用户只是打开设置看一眼、什么都没动，"
                  + "配置里不会多出显式值，设计基线以后改了仍然影响得到他。"
                : "  >>> 留空没有写回 null —— 用户的配置会被固化成一个显式值，之后再改设计基线也影响不到他。");

            // ---- 非法输入必须报错而不是静默丢弃 ----
            ProbeOutput.Section("非法输入的反馈");
            var before = File.ReadAllText(store.ConfigPath);
            window.SetDelaysForTest(200, 350);
            // 直接改控件文本，模拟用户敲了非数字
            window.SetDelaysForTest(null, null);
            SetRawDelayText(window, "abc", "400");

            var (badOk, badMessage) = window.SaveCurrentSettings();
            ProbeOutput.Kv("保存结果", badOk ? "成功（异常，应当失败）" : "失败（正确）");
            ProbeOutput.Kv("提示文本", window.SaveStatusTextForTest.Trim());
            ProbeOutput.Kv("配置文件未被改动", File.ReadAllText(store.ConfigPath) == before ? "是" : "否（异常）");

            results.Add(("非法输入被拦下且不落盘",
                !badOk && window.SaveStatusTextForTest.Contains("abc") && File.ReadAllText(store.ConfigPath) == before,
                window.SaveStatusTextForTest.Trim()));

            // ---- 收起延迟必须大于展开延迟 ----
            ProbeOutput.Section("收起快于展开时被拦下");
            window.SetDelaysForTest(400, 200);
            var (crossOk, _) = window.SaveCurrentSettings();
            ProbeOutput.Kv("保存结果", crossOk ? "成功（异常）" : "失败（正确）");
            ProbeOutput.Kv("提示文本", window.SaveStatusTextForTest.Trim());
            results.Add(("收起快于展开被拦下", !crossOk, window.SaveStatusTextForTest.Trim()));

            window.Close();
            store.Dispose();
            Report(results);
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("探针异常: " + ex);
            CrashLog.Write("settings-probe", ex);
            Report(results);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    /// <summary>直接改延迟输入框的文本，用来模拟用户敲了非数字。</summary>
    private static void SetRawDelayText(SettingsWindow window, string openText, string closeText)
    {
        // 走接缝传入"能解析"的值，再通过反射之外的方式覆盖不合适；
        // 这里用接缝的整数版传一个合法值，然后单独把 open 换成非法文本。
        window.SetDelaysForTest(0, 350);

        // 用 FindName 拿到输入框直接改文本 —— 这是探针里唯一直接碰控件的地方，
        // 因为"用户敲了非数字"这个场景没有别的办法模拟。
        if (window.FindName("OpenDelayBox") is System.Windows.Controls.TextBox box)
            box.Text = openText;
        if (window.FindName("CloseDelayBox") is System.Windows.Controls.TextBox closeBox)
            closeBox.Text = closeText;
    }

    private static void Report(List<(string Name, bool Passed, string Detail)> results)
    {
        ProbeOutput.Section("汇总");
        foreach (var (name, passed, detail) in results)
        {
            var suffix = detail.Length > 0 && !passed ? $"  ({detail})" : "";
            ProbeOutput.Kv(name, (passed ? "通过" : "失败") + suffix);
        }

        var allPassed = results.Count > 0 && results.All(r => r.Passed);
        ProbeOutput.Kv("总体", allPassed ? "全部通过" : "存在失败");
        ProbeOutput.Flush();
    }
}
