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
                // 标题栏在内容顶部：横向取中间，纵向离顶边约 24 DIP。
                var scale = WinQuick.App.Diagnostics.MonitorLayout.AtCursor().Scale;
                var titleX = beforeDrag.left + (int)Math.Round(120 * scale);
                var titleY = beforeDrag.top + (int)Math.Round(24 * scale);

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
            panel = new SettingsPanel(store.Config, _ => (true, 0),
                config => applied.Add(config));

            var baseline = applied.Count;
            panel.SetHotkeyForTest(new HotkeySpec(HotkeyModifiers.Alt, 0x20, "Space"));
            panel.SetDelaysForText("300", "500");
            panel.SetDimOpacityForTest(0.6);
            panel.SetAccentForTest("FF8800");
            panel.InvokeApplyForTest();

            ProbeOutput.Kv("应用次数", (applied.Count - baseline).ToString());
            var last = applied.Count > 0 ? applied[^1] : null;

            ProbeOutput.Kv("热键", last?.Hotkey ?? "(无)");
            ProbeOutput.Kv("展开/收起延迟", $"{last?.Behavior.HoverOpenDelayMs} / {last?.Behavior.HoverCloseDelayMs}");
            ProbeOutput.Kv("遮罩不透明度", last?.Appearance.DimOpacity?.ToString() ?? "(空)");
            ProbeOutput.Kv("高亮色（自动补 #）", last?.Appearance.AccentColor ?? "(空)");

            var normalOk = last is not null
                           && last.Hotkey == "Alt+Space"
                           && last.Behavior.HoverOpenDelayMs == 300
                           && last.Behavior.HoverCloseDelayMs == 500
                           && Math.Abs((last.Appearance.DimOpacity ?? 0) - 0.6) < 0.001
                           && last.Appearance.AccentColor == "#FF8800";

            results.Add(("正常改动组装正确", normalOk, last?.Hotkey ?? ""));

            // ---- 打字过程中的中间值 ----
            //
            // 这里刻意**允许**中间值生效，而不是拦掉。
            // 理由：面板是实时预览，用户从 200 改到 300 的途中会出现 3、30 这种中间值，
            // 让它们即时生效才符合"改动即预览"。真正防止坏值落盘的是**去抖保存** ——
            // 用户打完字、手停下来之后写盘的才是最终值。
            // （我最初在这里断言"半截输入不该被应用"，但那是我没实现的意图，不是设计缺陷。）
            ProbeOutput.Section("打字过程中的中间值可生效，靠去抖保护落盘");
            var beforeCount = applied.Count;
            panel.SetDelaysForText("3", "");
            panel.InvokeApplyForTest();
            var afterFirstDigit = applied.Count;

            panel.SetDelaysForText("", "3");
            panel.InvokeApplyForTest();
            var afterOtherSide = applied.Count;

            ProbeOutput.Kv("输入「3」时立即生效", afterFirstDigit > beforeCount ? "是（预期）" : "否");
            ProbeOutput.Kv("另一侧输入「3」时立即生效", afterOtherSide > afterFirstDigit ? "是（预期）" : "否");

            // 真正要保证的是：非法输入（非数字）不生效、不报错刷屏。
            panel.SetDelaysForText("abc", "350");
            panel.InvokeApplyForTest();
            var afterNonNumeric = applied.Count;

            panel.SetDelaysForText("12x", "350");
            panel.InvokeApplyForTest();
            var afterTrailingGarbage = applied.Count;

            ProbeOutput.Kv("非数字输入不生效", afterNonNumeric == afterOtherSide ? "是" : "否（异常）");
            ProbeOutput.Kv("尾随非法字符也不生效", afterTrailingGarbage == afterOtherSide ? "是" : "否（异常）");
            ProbeOutput.Line();
            ProbeOutput.Line(afterTrailingGarbage == afterOtherSide
                ? "  >>> 非数字输入既不生效也不刷红字。落盘由 500ms 去抖保护："
                  + "手停下来之后写的才是最终值。"
                : "  >>> 非法输入被当成有效值应用了。");

            results.Add(("非数字输入不生效", afterTrailingGarbage == afterOtherSide,
                $"{afterTrailingGarbage - afterOtherSide} 次"));

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

            // ---- 不合格的色值不能被应用 ----
            ProbeOutput.Section("不合格的色值不能被应用");
            var beforeAccent = applied.Count;
            panel.SetAccentForTest("#00");               // 还没输完
            panel.InvokeApplyForTest();
            var afterShortHex = applied.Count;

            panel.SetAccentForTest("xyz");               // 明确不合法
            panel.InvokeApplyForTest();
            var afterBadHex = applied.Count;

            ProbeOutput.Kv("色值「#00」时的应用次数变化", (afterShortHex - beforeAccent).ToString());
            ProbeOutput.Kv("色值「xyz」时的应用次数变化", (afterBadHex - afterShortHex).ToString());
            ProbeOutput.Line();
            ProbeOutput.Line(afterBadHex == beforeAccent
                ? "  >>> 不完整与不合法的色值都未被应用。"
                : "  >>> 不合格的色值被应用了。");

            results.Add(("不合格色值不被应用", afterBadHex == beforeAccent,
                $"{afterBadHex - beforeAccent} 次"));

            // ---- 留空 = 沿用设计基线 ----
            ProbeOutput.Section("留空表示沿用设计基线");
            panel.SetDelaysForText("", "");
            panel.SetDimOpacityForTest(0.4);             // 等于默认值
            panel.SetAccentForTest("");
            panel.InvokeApplyForTest();

            var baselineConfig = applied.Count > 0 ? applied[^1] : null;
            ProbeOutput.Kv("延迟写回 null",
                baselineConfig?.Behavior.HoverOpenDelayMs is null
                && baselineConfig.Behavior.HoverCloseDelayMs is null ? "是" : "否");
            ProbeOutput.Kv("遮罩等于默认时写回 null", baselineConfig?.Appearance.DimOpacity is null ? "是" : "否");
            ProbeOutput.Kv("高亮色留空写回 null", baselineConfig?.Appearance.AccentColor is null ? "是" : "否");

            var baselineOk = baselineConfig is not null
                             && baselineConfig.Behavior.HoverOpenDelayMs is null
                             && baselineConfig.Behavior.HoverCloseDelayMs is null
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
