using System.IO;
using System.Windows.Interop;
using WinQuick.App.Config;
using WinQuick.App.Diagnostics;
using WinQuick.App.Radial;
using WinQuick.Core.Config;
using WinQuick.Core.Hotkey;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinQuick.App.M0;

/// <summary>
/// 设置面板探针。
///
/// 界面好不好看只能靠人看，但这几件事可以程序化核对，而且恰恰是风险最高的：
/// 1. **XAML 与令牌资源能否加载** —— 控件名对不上、DynamicResource 键写错，
///    都会在构造窗口时直接抛异常，而这类错误只在真正打开面板时才暴露。
/// 2. **「改动即应用」的语义** —— 面板没有保存按钮，每次改动都组装配置并回传。
///    要确认：正常情况下组装正确；**输入到一半不能被应用**，
///    否则用户打字过程中会不停写入坏配置。
/// 3. **留空 = 沿用设计基线** —— 打开面板一眼什么都没改，不该留下显式值。
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

            ProbeOutput.Section("面板能否加载");
            SettingsPanel? panel;
            try
            {
                panel = new SettingsPanel(
                    store.Config,
                    probeHotkey: spec => (HotkeySpec.TryParse(spec.ToString(), out _, out _), 0),
                    applyConfig: _ => { });

                ProbeOutput.Kv("构造成功（XAML 与令牌资源均可用）", "是");
                results.Add(("XAML 与令牌加载", true, ""));
            }
            catch (Exception ex)
            {
                ProbeOutput.Kv("构造失败", ex.Message);
                results.Add(("XAML 与令牌加载", false, ex.Message));
                Report(results);
                return;
            }

            // 真正显示一次 —— 构造成功不代表能显示（布局异常、资源缺失只在显示时暴露）。
            var shown = false;
            var handleCreated = false;
            try
            {
                panel.Show();
                await Task.Delay(400);

                shown = panel.IsVisible;
                handleCreated = new WindowInteropHelper(panel).Handle != IntPtr.Zero;

                ProbeOutput.Kv("显示成功", shown ? "是" : "否");
                ProbeOutput.Kv("窗口句柄已创建", handleCreated ? "是" : "否");
                ProbeOutput.Kv("面板尺寸", $"{panel.ActualWidth:0} x {panel.ActualHeight:0}");

                var self = (HWND)new WindowInteropHelper(panel).Handle;
                var fg = PInvoke.GetForegroundWindow();
                ProbeOutput.Kv("面板取得前台（可输入）", fg == self ? "是" : "否");

                // 置顶是硬需求：遮罩与轮盘都是置顶的，面板不置顶就会被压在遮罩下面，
                // 既看不见也点不到 —— 用户报的正是这个现象。
                var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(self, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                var topmost = ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST);
                ProbeOutput.Kv("面板已置顶（WS_EX_TOPMOST）", topmost ? "是" : "否（会被遮罩盖住）");
                results.Add(("面板已置顶", topmost, ""));
            }
            catch (Exception ex)
            {
                ProbeOutput.Kv("显示失败", ex.Message);
            }

            results.Add(("面板可显示", shown && handleCreated, $"{panel.ActualWidth:0}x{panel.ActualHeight:0}"));

            // ---- 标题栏拖动 ----
            // 这一条对应"实测拖不动"的反馈：最初用 Window.DragMove()，它要求窗口处于
            // 激活状态、不满足就抛异常，而异常被静默吞掉 —— 表现是"按下去毫无反应"。
            // 现在自己实现拖动，于是**可以用真实鼠标消息验**：
            // 投递按下 → 移指针 → 抬起，窗口应当跟着挪。
            ProbeOutput.Section("标题栏拖动面板");

            var panelHwnd = (Windows.Win32.Foundation.HWND)panel.HandleValue;
            if (panelHwnd != default && Windows.Win32.PInvoke.GetWindowRect(panelHwnd, out var beforeDrag))
            {
                // 拖拽点直接向标题栏控件本人要屏幕坐标（取它左上角内侧一点）。
                // 原来写死「左边 +120 DIP」—— 面板改成侧栏布局后那里是侧栏而不是标题栏，
                // 拖动悄悄失效（移动 (0,0)）。布局常量不该出现在这种核对里。
                var scale = WinQuick.App.Diagnostics.MonitorLayout.AtCursor().Scale;
                var titlePoint = panel.TitleBar.PointToScreen(new System.Windows.Point(40, 12));
                var titleX = (int)Math.Round(titlePoint.X);
                var titleY = (int)Math.Round(titlePoint.Y);

                // 先激活面板：真实使用时用户点它就会激活它。
                // 探针里不激活的话，投递的鼠标消息与真实场景不等价
                //（而且这正好是"DragMove 拖不动"的可疑原因：面板一激活就被
                //  覆盖层的置顶重申抢走前台，于是窗口在按下那一刻并不是激活的）。
                Windows.Win32.PInvoke.SetForegroundWindow(panelHwnd);
                await Task.Delay(300);
                ProbeOutput.Kv("面板是否已激活", panel.IsActive ? "是" : "否");

                // 记住原光标位置：这一节会把指针挪到面板上并真的按一下，收尾要还回去。
                Windows.Win32.PInvoke.GetCursorPos(out var cursorBefore);
                Windows.Win32.PInvoke.SetCursorPos(titleX, titleY);
                await Task.Delay(200);

                // 用 SendInput 合成**真实**的按键事件，而不是 PostMessage。
                //
                // 这是被数据逼出来的：投递的 WM_LBUTTONDOWN 在面板这条路径上收不到 ——
                // WPF 会拿**真实按键状态**校验鼠标消息，投递的按下与真实状态不一致时会被丢掉。
                // 真实的按下会同时改变按键状态，WPF 才认。
                SendMouse(LeftDown: true);
                await Task.Delay(150);

                ProbeOutput.Kv("按下后是否进入拖动状态", panel.IsDraggingForTest ? "是" : "否");

                // 移动指针：真实移动，WPF 才会派发 MouseMove 给拖动逻辑
                var dx = (int)Math.Round(60 * scale);
                var dy = (int)Math.Round(40 * scale);
                Windows.Win32.PInvoke.SetCursorPos(titleX + dx, titleY + dy);
                await Task.Delay(250);

                SendMouse(LeftDown: false);
                await Task.Delay(250);

                Windows.Win32.PInvoke.GetWindowRect(panelHwnd, out var afterDrag);
                var movedX = afterDrag.left - beforeDrag.left;
                var movedY = afterDrag.top - beforeDrag.top;

                ProbeOutput.Kv("面板移动量", $"({movedX}, {movedY})，期望约 ({dx}, {dy})");
                ProbeOutput.Kv("拖动结束后 PlacedByUser", panel.PlacedByUserForTest ? "是（不再自动摆位）" : "否");

                results.Add(("标题栏拖动真的能挪动面板",
                    Math.Abs(movedX - dx) <= 3 && Math.Abs(movedY - dy) <= 3,
                    $"移动 ({movedX}, {movedY})，期望 ({dx}, {dy})"));

                results.Add(("拖过之后标记为已拖动（不再自动摆位）",
                    panel.PlacedByUserForTest, ""));

                Windows.Win32.PInvoke.SetCursorPos(cursorBefore.X, cursorBefore.Y);
            }
            else
            {
                results.Add(("标题栏拖动真的能挪动面板", false, "取不到面板窗口矩形"));
            }


            // ---- 改动即应用 ----
            ProbeOutput.Section("改动即应用的语义");

            var applied = new List<AppConfig>();
            // _original 故意带着显式悬停延迟值：界面里那两个输入框已经删了，
            // 但老配置里的显式值必须**原样带过去** —— 打开一次设置面板不该清掉用户的既有配置。
            var withLegacyDelays = store.Config with
            {
                Behavior = store.Config.Behavior with
                {
                    HoverOpenDelayMs = 300,
                    HoverCloseDelayMs = 500,
                },
            };
            panel = new SettingsPanel(withLegacyDelays, _ => (true, 0),
                config => applied.Add(config));

            var baseline = applied.Count;
            panel.SetHotkeyForTest(new HotkeySpec(HotkeyModifiers.Alt, 0x20, "Space"));
            panel.SetDimOpacityForTest(0.6);
            panel.SetAccentForTest("FF8800");
            panel.InvokeApplyForTest();

            ProbeOutput.Kv("应用次数", (applied.Count - baseline).ToString());
            var last = applied.Count > 0 ? applied[^1] : null;

            ProbeOutput.Kv("热键", last?.Hotkey ?? "(无)");
            ProbeOutput.Kv("旧配置的悬停延迟（应原样保留）",
                $"{last?.Behavior.HoverOpenDelayMs} / {last?.Behavior.HoverCloseDelayMs}");
            ProbeOutput.Kv("遮罩不透明度", last?.Appearance.DimOpacity?.ToString() ?? "(空)");
            ProbeOutput.Kv("高亮色（自动补 #）", last?.Appearance.AccentColor ?? "(空)");

            var normalOk = last is not null
                           && last.Hotkey == "Alt+Space"
                           && last.Behavior.HoverOpenDelayMs == 300
                           && last.Behavior.HoverCloseDelayMs == 500
                           && Math.Abs((last.Appearance.DimOpacity ?? 0) - 0.6) < 0.001
                           && last.Appearance.AccentColor == "#FF8800";

            results.Add(("正常改动组装正确（含旧延迟值不被冲掉）", normalOk, last?.Hotkey ?? ""));

            // ---- 热键预设：走真实的 OnPresetChanged 路径 ---------------------
            ProbeOutput.Section("热键预设下拉");
            var beforePreset = applied.Count;
            panel.SelectPresetForTest("Ctrl+Alt+Q");
            var presetHotkey = applied.Count > beforePreset ? applied[^1].Hotkey : null;
            ProbeOutput.Kv("选 Ctrl+Alt+Q 后应用的热键", presetHotkey ?? "(没有)");
            results.Add(("选中预设即填入并应用（探测通过时）",
                presetHotkey == "Ctrl+Alt+Q", presetHotkey ?? "无"));

            panel.SelectPresetForTest("Ctrl+Shift+Z");
            var userNamed = applied.Count > 0 ? applied[^1].Hotkey : null;
            ProbeOutput.Kv("选用户点名的 Ctrl+Shift+Z 后应用的热键", userNamed ?? "(没有)");
            results.Add(("用户点名的 Ctrl+Shift+Z 在预设里且可选可应用",
                userNamed == "Ctrl+Shift+Z", userNamed ?? "无"));

            // ---- 热键预设清单本身的健全性（下拉里出现什么由它决定） ----
            ProbeOutput.Section("预设清单健全性");
            var badPresets = HotkeySpec.RecommendedPresets
                .Where(p => !HotkeySpec.TryParse(p, out _, out _)
                            || HotkeySpec.TryParse(p, out var spec, out _) && spec!.Modifiers == HotkeyModifiers.Win)
                .ToList();
            ProbeOutput.Kv("预设数", HotkeySpec.RecommendedPresets.Length.ToString());
            ProbeOutput.Kv("解析失败或纯 Win 的预设", badPresets.Count == 0 ? "没有" : string.Join("、", badPresets));
            results.Add(("预设全部可解析且不踩纯 Win 组合", badPresets.Count == 0,
                string.Join(" / ", HotkeySpec.RecommendedPresets)));

            // ---- 走真实集成路径：面板能否取得前台 ----
            //
            // 上面是直接 new + Show，绕过了生产路径里的显式前台获取。
            // 而"面板能不能输入"是硬需求（热键录制、文本框都要键盘），所以这里走一遍真实路径。
            ProbeOutput.Section("真实集成路径：覆盖层 + 面板");
            using (var overlay = new Hosting.OverlayController(store.Menu))
            {
                overlay.SetSettingsSource(store.Config);
                overlay.SetHotkeyProbe(_ => (true, 0));

                overlay.Prewarm();
                overlay.Show();
                await Task.Delay(400);
                ProbeOutput.Kv("覆盖层已显示", overlay.IsVisible ? "是" : "否");
                ProbeOutput.Kv("轮盘实际可见", PInvoke.IsWindowVisible((HWND)overlay.RadialHandle) ? "是" : "否");

                overlay.OpenSettings();
                await Task.Delay(500);

                ProbeOutput.Kv("面板已打开", overlay.IsSettingsOpen ? "是" : "否");
                ProbeOutput.Kv("轮盘仍然可见（改动可即时预览）",
                    PInvoke.IsWindowVisible((HWND)overlay.RadialHandle) ? "是" : "否");

                var panelHandle = (HWND)overlay.SettingsPanelHandle;
                ProbeOutput.Kv("面板实际可见", PInvoke.IsWindowVisible(panelHandle) ? "是" : "否");

                // 面板必须完整落在屏内：比屏幕还高的话底部按钮会被截掉、点不到。
                if (PInvoke.GetWindowRect(panelHandle, out var panelRect))
                {
                    var mon = MonitorLayout.AtCursor();
                    var fits = panelRect.top >= mon.Y && panelRect.bottom <= mon.Bottom
                               && panelRect.left >= mon.X && panelRect.right <= mon.Right;

                    ProbeOutput.Kv("面板尺寸（物理像素）",
                        $"{panelRect.right - panelRect.left} x {panelRect.bottom - panelRect.top}");
                    ProbeOutput.Kv("屏幕可用高度（物理像素）", mon.Height.ToString());
                    ProbeOutput.Kv("完整落在屏内", fits ? "是" : "否（底部按钮可能点不到）");
                    results.Add(("面板完整落在屏内", fits,
                        $"{panelRect.bottom - panelRect.top} vs {mon.Height}"));
                }
                ProbeOutput.Kv("面板取得前台（可输入）",
                    PInvoke.GetForegroundWindow() == panelHandle ? "是" : "否");

                var compositeOk = overlay.IsSettingsOpen
                                  && PInvoke.IsWindowVisible((HWND)overlay.RadialHandle)
                                  && PInvoke.GetForegroundWindow() == panelHandle;

                results.Add(("面板开在覆盖层里且可输入", compositeOk, ""));

                // 关面板后轮盘要重新拿到焦点，否则 Esc 就失效了。
                overlay.CloseSettings();
                await Task.Delay(300);
                ProbeOutput.Kv("关闭后轮盘重获前台",
                    PInvoke.GetForegroundWindow() == (HWND)overlay.RadialHandle ? "是" : "否");

                results.Add(("关面板后轮盘重获焦点",
                    PInvoke.GetForegroundWindow() == (HWND)overlay.RadialHandle, ""));

                overlay.Hide();
            }

            ProbeOutput.Line();
            ProbeOutput.Line("  这样设计的效果：拖遮罩滑块、改高亮色、调悬停延迟，");
            ProbeOutput.Line("  都能立刻在旁边的轮盘上看到 —— 独立对话框做不到，它会占住前台、覆盖层已经关掉。");

            // ---- 不合格的色值进不来（调色盘语义） ----
            // 原来测的是文本框打字护栏；换调色盘后自由输入没了，
            // 对应的保证是「接缝/HSV 输入收到非法值时当前选择不变」。
            ProbeOutput.Section("不合格的色值进不来");
            var accentBefore = panel.BuildConfigForTest()?.Appearance.AccentColor;
            panel.SetAccentForTest("#00");               // 半截
            var afterPartial = panel.BuildConfigForTest()?.Appearance.AccentColor;
            panel.SetAccentForTest("xyz");               // 明确不合法
            var afterBad = panel.BuildConfigForTest()?.Appearance.AccentColor;

            ProbeOutput.Kv("之前的选择", accentBefore ?? "(默认)");
            ProbeOutput.Kv("塞「#00」之后", afterPartial ?? "(默认)");
            ProbeOutput.Kv("塞「xyz」之后", afterBad ?? "(默认)");
            results.Add(("半截与非法色值不改变当前选择",
                afterPartial == accentBefore && afterBad == accentBefore,
                $"{accentBefore} → {afterPartial} → {afterBad}"));

            // ---- 调色盘提交：最近使用会更新并进配置 ----
            ProbeOutput.Section("调色盘提交与最近使用");
            panel.CommitAccentForTest("#123456");
            var recent1 = panel.BuildConfigForTest()?.Appearance.RecentAccentColors.ToList() ?? [];
            panel.CommitAccentForTest("#654321");
            panel.CommitAccentForTest("#123456");        // 再选一次：去重并顶到最前
            var recent3 = panel.BuildConfigForTest()?.Appearance.RecentAccentColors.ToList() ?? [];

            ProbeOutput.Kv("第一次提交后", string.Join(",", recent1));
            ProbeOutput.Kv("三次提交后", string.Join(",", recent3));
            results.Add(("提交即入「最近使用」：置前、去重",
                recent1.FirstOrDefault() == "#123456"
                && recent3.FirstOrDefault() == "#123456"
                && recent3.Count(r => r == "#123456") == 1
                && recent3.Contains("#654321"),
                string.Join(",", recent3)));

            // ---- 留空 = 沿用设计基线 ----
            ProbeOutput.Section("留空表示沿用设计基线");
            panel.SetDimOpacityForTest(0.4);             // 等于默认值
            panel.SetAccentForTest("");
            panel.InvokeApplyForTest();

            var baselineConfig = applied.Count > 0 ? applied[^1] : null;
            // 用模式匹配而不是 ?. 一次、下一行又直接取成员 —— 后者正是可空分析的经典漏网点。
            var behavior = baselineConfig?.Behavior;
            // 悬停延迟的「留空写回 null」已经不适用：界面删了，值从 _original 原样带过去
            //（上面那节核对的就是它），不会因为清空输入框而落成 null。
            ProbeOutput.Kv("遮罩等于默认时写回 null", baselineConfig?.Appearance.DimOpacity is null ? "是" : "否");
            ProbeOutput.Kv("高亮色留空写回 null", baselineConfig?.Appearance.AccentColor is null ? "是" : "否");

            var baselineOk = baselineConfig is not null
                             && baselineConfig.Appearance.DimOpacity is null
                             && baselineConfig.Appearance.AccentColor is null;

            results.Add(("留空写回 null", baselineOk, ""));
            ProbeOutput.Line();
            ProbeOutput.Line(baselineOk
                ? "  >>> 留空即沿用设计基线：打开面板看一眼、什么都没改，"
                  + "配置里不会多出显式值，设计基线以后改了仍然影响得到他。"
                : "  >>> 留空没有写回 null —— 用户的配置会被固化成一个显式值。");

            // 菜单项的增删改**不在这里核对**了：
            // 那些控件已经从面板里移走（改为在轮盘上直接编辑），
            // 规则本身（上限、不能删到空、索引合法性）是纯函数，由 MenuEditor 的单测覆盖；
            // 从点击到落盘的整条路径由 --add-sector-probe 覆盖。
            // 留在这里的只是"配置能写盘并读回"这一条，它仍然属于设置面板的职责。

            ProbeOutput.Section("配置落盘与读回");
            var probeMenu = store.Menu with
            {
                Items = store.Menu.Items.Append(new MenuItemSpec
                {
                    Label = "探针写盘项",
                    Action = MenuAction.Launch("cmd.exe"),
                }).ToList(),
            };

            var menuSaved = store.SaveMenu(probeMenu, out var saveError);
            ProbeOutput.Kv("写入 menus.json", menuSaved ? "是" : $"否：{saveError}");

            var reloaded = MenuConfigFile.Parse(File.ReadAllText(store.MenusPath));
            var reloadedMenu = reloaded.Menus.FirstOrDefault();
            ProbeOutput.Kv("读回后的项数", reloadedMenu?.Items.Count.ToString() ?? "(无)");
            ProbeOutput.Kv("读回后最后一项", reloadedMenu?.Items.LastOrDefault()?.Label ?? "(无)");

            results.Add(("菜单能写盘并读回",
                menuSaved && reloadedMenu?.Items.Count == probeMenu.Items.Count,
                $"{store.Menu.Items.Count} → {reloadedMenu?.Items.Count.ToString() ?? "无"}"));

            panel.Close();
            store.Dispose();
            Report(results);
        }
        catch (Exception ex)
        {
            ProbeOutput.Line("探针异常: " + ex);
            CrashLog.Write("settings-probe", ex);

            // 异常必须记成一条失败结果。
            // 否则会出现"探针崩了、却报总体全部通过"—— 那比没有探针更糟，
            // 因为它会让人以为这块已经验证过了。
            results.Add(("探针未抛异常", false, ex.GetType().Name + ": " + ex.Message));
            Report(results);
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    /// <summary>合成一次真实的鼠标按下/抬起（SendInput），而不是投递消息。</summary>
    private static void SendMouse(bool LeftDown)
    {
        var flag = LeftDown
            ? Windows.Win32.UI.Input.KeyboardAndMouse.MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN
            : Windows.Win32.UI.Input.KeyboardAndMouse.MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP;

        var input = new Windows.Win32.UI.Input.KeyboardAndMouse.INPUT
        {
            type = Windows.Win32.UI.Input.KeyboardAndMouse.INPUT_TYPE.INPUT_MOUSE,
            Anonymous = new Windows.Win32.UI.Input.KeyboardAndMouse.INPUT._Anonymous_e__Union
            {
                mi = new Windows.Win32.UI.Input.KeyboardAndMouse.MOUSEINPUT { dwFlags = flag },
            },
        };

        Windows.Win32.PInvoke.SendInput(
            [input],
            System.Runtime.InteropServices.Marshal.SizeOf<Windows.Win32.UI.Input.KeyboardAndMouse.INPUT>());
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
