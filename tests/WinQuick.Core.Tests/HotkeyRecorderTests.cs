using System.Windows.Input;
using WinQuick.App.Settings;
using WinQuick.Core.Hotkey;

namespace WinQuick.Core.Tests;

/// <summary>
/// 热键录制：把界面上按下的键翻译成热键模型。
///
/// 这块原本是测试盲区（逻辑在设置窗口的 code-behind 里，绑着控件）。
/// 但它恰恰是用户最容易踩坑的地方 —— 按了不支持的键、或忘了带修饰键，
/// 如果翻译错了，用户会在设置界面里得到一个"看起来对、其实注册不了"的组合。
/// </summary>
public class HotkeyRecorderTests
{
    [Theory]
    [InlineData(Key.W, ModifierKeys.Control | ModifierKeys.Windows, "Ctrl+Win+W")]
    [InlineData(Key.W, ModifierKeys.Alt, "Alt+W")]
    [InlineData(Key.Space, ModifierKeys.Control | ModifierKeys.Alt, "Ctrl+Alt+Space")]
    [InlineData(Key.F5, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+F5")]
    [InlineData(Key.D7, ModifierKeys.Windows, "Win+7")]
    public void CapturesCombination_AndFormatsInFixedOrder(
        Key key, ModifierKeys modifiers, string expected)
    {
        Assert.True(HotkeyRecorder.TryCapture(key, modifiers, out var spec, out var error), error);
        Assert.NotNull(spec);
        // 顺序固定为 Ctrl → Win → Alt → Shift，界面显示与 HotkeySpec.ToString 一致。
        Assert.Equal(expected, spec.ToString());
    }

    [Fact]
    public void ModifierKeyAlone_IsNotAnError_JustIncomplete()
    {
        // 只按 Ctrl 是"还没输完"，不是错误。若报成错误，用户会在按住 Ctrl 的瞬间
        // 看到一条红字，以为操作出了问题。
        Assert.False(HotkeyRecorder.TryCapture(Key.LeftCtrl, ModifierKeys.Control, out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("请再按一个主键", error);

        // 任何修饰键都一样
        Assert.False(HotkeyRecorder.TryCapture(Key.LWin, ModifierKeys.Windows, out _, out _));
        Assert.False(HotkeyRecorder.TryCapture(Key.LeftAlt, ModifierKeys.Alt, out _, out _));
    }

    [Fact]
    public void UnsupportedKey_ExplainsWhatIsSupported()
    {
        // 例如多媒体键、浏览器键这类。错误信息要告诉用户能用什么，
        // 否则用户只知道"不行"，不知道该怎么办。
        Assert.False(HotkeyRecorder.TryCapture(Key.BrowserHome, ModifierKeys.Control, out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("不支持", error);
        Assert.Contains("F1", error);   // 提示里列出了可用的键类别
    }

    [Fact]
    public void MissingModifier_IsRejectedByTheSharedParser()
    {
        // 「必须带修饰键」这条规则只在 HotkeySpec.TryParse 里实现一次，
        // 录制器复用它的结果而不是自己再判一遍 —— 否则两处规则迟早分叉。
        Assert.False(HotkeyRecorder.TryCapture(Key.F5, ModifierKeys.None, out _, out var error));

        Assert.NotNull(error);
        Assert.Contains("修饰键", error);
    }

    [Theory]
    [InlineData(Key.OemMinus, "Ctrl+Minus")]      // 用户看到的是 -，不是 OemMinus
    [InlineData(Key.OemTilde, "Ctrl+`")]
    [InlineData(Key.OemComma, "Ctrl+,")]
    [InlineData(Key.OemQuotes, "Ctrl+'")]
    public void OemKeys_ShowTheSymbolNotTheWpfName(Key key, string expected)
    {
        Assert.True(HotkeyRecorder.TryCapture(key, ModifierKeys.Control, out var spec, out _));
        Assert.Equal(expected, spec!.ToString());
    }

    [Theory]
    [InlineData(Key.A, "Ctrl+A")]
    [InlineData(Key.Z, "Ctrl+Z")]
    [InlineData(Key.D0, "Ctrl+0")]
    [InlineData(Key.D9, "Ctrl+9")]
    [InlineData(Key.F1, "Ctrl+F1")]
    [InlineData(Key.F12, "Ctrl+F12")]
    [InlineData(Key.F24, "Ctrl+F24")]
    public void LetterDigitAndFunctionKeys_RoundTrip(Key key, string expected)
    {
        Assert.True(HotkeyRecorder.TryCapture(key, ModifierKeys.Control, out var spec, out var error), error);
        Assert.Equal(expected, spec!.ToString());
    }

    [Fact]
    public void CapturedSpec_IsAcceptedByTheParserAndIsRegistrable()
    {
        // 录制出来的组合必须能被解析器重新解析 ——
        // 否则会出现"界面上显示 Ctrl+Win+W，写进配置后却解析失败"。
        Assert.True(HotkeyRecorder.TryCapture(
            Key.W, ModifierKeys.Control | ModifierKeys.Windows, out var spec, out _));

        Assert.True(HotkeySpec.TryParse(spec!.ToString(), out var reparsed, out var error), error);
        Assert.Equal(spec, reparsed);
    }
}
