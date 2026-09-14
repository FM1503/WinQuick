using System.Windows.Input;
using WinQuick.Core.Hotkey;

namespace WinQuick.App.Settings;

/// <summary>
/// 把 WPF 的按键事件翻译成热键模型。
///
/// WPF 的 <see cref="Key"/> 与虚拟键码是两套命名（例如 OemMinus 对应 VK_OEM_MINUS），
/// 而热键解析器认的是人类可读的键名。这里做一层翻译，并复用
/// <see cref="HotkeySpec.TryParse"/> 的校验 —— 不在这里重复实现一遍规则，
/// 否则两处规则迟早会分叉（比如"必须带修饰键"这条只在解析器里改了就漏了）。
/// </summary>
public static class HotkeyRecorder
{
    /// <summary>
    /// 尝试把一次按键转成热键。返回 false 时会给出可读原因，
    /// 由界面直接显示 —— 用户按了不支持的键却毫无反馈是最让人困惑的。
    /// </summary>
    public static bool TryCapture(Key key, ModifierKeys modifiers, out HotkeySpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (!TryKeyToken(key, out var token))
        {
            error = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
                or Key.System or Key.None
                // 只按修饰键本身是"还没输完"，不是错误。
                ? "请再按一个主键（例如 W、F5、Space）"
                : $"不支持这个按键：{key}。可以试试字母、数字、F1–F24、空格或常见符号。";
            return false;
        }

        var text = BuildText(modifiers, token);
        return HotkeySpec.TryParse(text, out spec, out error);
    }

    private static string BuildText(ModifierKeys modifiers, string token)
    {
        var parts = new List<string>(4);

        // 顺序固定为 Ctrl → Win → Alt → Shift，与 HotkeySpec.ToString 一致，
        // 这样界面上显示的字符串不会再被规范化成另一种写法。
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");

        parts.Add(token);
        return string.Join('+', parts);
    }

    /// <summary>WPF 按键 → 解析器认识的键名。</summary>
    private static bool TryKeyToken(Key key, out string token)
    {
        token = "";

        // 字母与数字
        if (key is >= Key.A and <= Key.Z) { token = ((char)('A' + (key - Key.A))).ToString(); return true; }
        if (key is >= Key.D0 and <= Key.D9) { token = ((char)('0' + (key - Key.D0))).ToString(); return true; }
        if (key is >= Key.NumPad0 and <= Key.NumPad9) { token = "Num" + (key - Key.NumPad0); return true; }

        // 功能键
        if (key is >= Key.F1 and <= Key.F24) { token = "F" + (key - Key.F1 + 1); return true; }

        token = key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Escape => "Escape",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",

            // OEM 键：WPF 的名字（OemMinus）与用户看到的符号（-）差得很远，
            // 所以转成符号本身，界面上显示的才是用户按的那个键。
            Key.OemMinus or Key.Subtract => "Minus",
            Key.OemPlus or Key.Add => "Equals",
            Key.OemComma => ",",
            Key.OemPeriod or Key.Decimal => ".",
            Key.OemQuestion or Key.Divide => "/",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemQuotes => "'",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.OemSemicolon => ";",

            _ => "",
        };

        return token.Length > 0;
    }
}
