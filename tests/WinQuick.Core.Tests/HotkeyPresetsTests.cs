using WinQuick.Core.Hotkey;

namespace WinQuick.Core.Tests;

/// <summary>
/// 热键预设清单的回归锁 —— 这份清单直接决定设置界面下拉里出现什么。
/// 「能解析、带修饰键、不踩已知冲突组合」三条少一条，用户就会在下拉里选到
/// 一个注册不上或按下去出别的事的组合。
/// </summary>
public class HotkeyPresetsTests
{
    [Fact]
    public void 每个预设都能解析且带修饰键()
    {
        foreach (var preset in HotkeySpec.RecommendedPresets)
        {
            var ok = HotkeySpec.TryParse(preset, out var spec, out var error);
            Assert.True(ok, $"{preset}：{error}");
            Assert.NotNull(spec);
            Assert.NotEqual(HotkeyModifiers.None, spec!.Modifiers);
        }
    }

    [Fact]
    public void 预设不含纯Win组合_不含输入法与系统冲突组合()
    {
        foreach (var preset in HotkeySpec.RecommendedPresets)
        {
            var spec = HotkeySpec.TryParse(preset, out var parsed, out _) ? parsed : null;
            Assert.NotNull(spec);

            // 纯 Win：Win11 shell 大多预留（程序自己也会警告）
            var winOnly = spec!.Modifiers == HotkeyModifiers.Win;
            Assert.False(winOnly, $"{preset} 只带 Win，会被系统预留");

            // 已知冲突：输入法中英切换、系统级组合
            Assert.NotEqual("Ctrl+Space", preset);
            Assert.NotEqual("Alt+Tab", preset);
            Assert.NotEqual("Alt+F4", preset);
        }
    }

    [Fact]
    public void 用户点名的CtrlShiftZ在清单里()
    {
        Assert.Contains("Ctrl+Shift+Z", HotkeySpec.RecommendedPresets);
    }

    [Fact]
    public void 默认热键也在预设里_且ToString能整串解析回来()
    {
        Assert.Contains(HotkeySpec.Default.ToString(), HotkeySpec.RecommendedPresets);

        foreach (var preset in HotkeySpec.RecommendedPresets)
        {
            var spec = HotkeySpec.TryParse(preset, out var parsed, out _) ? parsed : null;
            Assert.NotNull(spec);
            Assert.Equal(preset, spec!.ToString());   // 往返一致：选中预设后写进配置的就是原字符串
        }
    }
}
