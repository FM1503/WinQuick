namespace WinQuick.Core.Hotkey;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 一个全局热键的定义：修饰键 + 主键。
///
/// 为什么要有自己的模型而不是直接存 Win32 结构：
/// 它要同时服务于配置文件的字符串形式（"Ctrl+Win+W"）、设置界面的录制控件、
/// 以及 Win32 的 `MOD_* | VK` 三种表示，中间必须有可单测的转换。
/// </summary>
public sealed record HotkeySpec(HotkeyModifiers Modifiers, uint VirtualKey, string KeyName)
{
    /// <summary>是否含 Win 键。含 Win 的组合在 Win11 上大概率被 shell 预留，用于给出针对性提示。</summary>
    public bool UsesWinKey => Modifiers.HasFlag(HotkeyModifiers.Win);

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName);
        return string.Join("+", parts);
    }

    /// <summary>默认热键。Ctrl+Win+W 在目标机器上实测可注册（见 docs/M0-findings.md）。</summary>
    public static HotkeySpec Default { get; } =
        new(HotkeyModifiers.Control | HotkeyModifiers.Win, 0x57, "W");

    public static bool TryParse(string? text, out HotkeySpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键为空";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "热键为空";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var keyToken = parts[^1];

        foreach (var token in parts[..^1])
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "super" or "meta": modifiers |= HotkeyModifiers.Win; break;
                default:
                    error = $"无法识别的修饰键 “{token}”";
                    return false;
            }
        }

        if (!TryResolveKey(keyToken, out var vk, out var canonical))
        {
            error = $"无法识别的主键 “{keyToken}”";
            return false;
        }

        // 单独一个无修饰键的按键做全局热键会吞掉全系统的该按键，必须禁止。
        if (modifiers == HotkeyModifiers.None)
        {
            error = "全局热键必须至少包含一个修饰键（Ctrl / Alt / Shift / Win），否则会吞掉整个系统的该按键";
            return false;
        }

        // Win 单独作修饰键时，系统要求主键是字母/数字/功能键，且 Win 键必须最后解析。
        // 这里只做最基本的拦截：Win+Esc / Win+F4 之类的组合系统不会给我们。
        if (modifiers == HotkeyModifiers.Win && canonical is "Escape")
        {
            error = "Win+Esc 由系统保留";
            return false;
        }

        spec = new HotkeySpec(modifiers, vk, canonical);
        return true;
    }

    private static bool TryResolveKey(string token, out uint vk, out string canonical)
    {
        vk = 0;
        canonical = token.ToUpperInvariant();

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; canonical = c.ToString(); return true; }
            if (c is >= '0' and <= '9') { vk = c; canonical = c.ToString(); return true; }
        }

        var upper = token.ToUpperInvariant();
        if (upper.StartsWith('F') && int.TryParse(upper[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            canonical = "F" + fn;
            return true;
        }

        var named = upper switch
        {
            "SPACE" => (0x20u, "Space"),
            "ENTER" or "RETURN" => (0x0Du, "Enter"),
            "TAB" => (0x09u, "Tab"),
            "ESC" or "ESCAPE" => (0x1Bu, "Escape"),
            "BACKSPACE" or "BACK" => (0x08u, "Backspace"),
            "DELETE" or "DEL" => (0x2Eu, "Delete"),
            "INSERT" or "INS" => (0x2Du, "Insert"),
            "HOME" => (0x24u, "Home"),
            "END" => (0x23u, "End"),
            "PAGEUP" or "PGUP" => (0x21u, "PageUp"),
            "PAGEDOWN" or "PGDN" => (0x22u, "PageDown"),
            "UP" => (0x26u, "Up"),
            "DOWN" => (0x28u, "Down"),
            "LEFT" => (0x25u, "Left"),
            "RIGHT" => (0x27u, "Right"),
            "`" or "BACKTICK" => (0xC0u, "`"),
            "-" or "MINUS" => (0xBDu, "Minus"),
            "=" or "EQUALS" => (0xBBu, "Equals"),
            "[" => (0xDBu, "["),
            "]" => (0xDDu, "]"),
            "\\" => (0xDCu, "\\"),
            ";" => (0xBAu, ";"),
            "'" => (0xDEu, "'"),
            "," => (0xBCu, ","),
            "." or "PERIOD" => (0xBEu, "."),
            "/" or "SLASH" => (0xBFu, "/"),
            _ => (0u, ""),
        };

        if (named.Item1 == 0) return false;
        vk = named.Item1;
        canonical = named.Item2;
        return true;
    }

    /// <summary>
    /// 命中 Win11 上「被 shell 预留」的典型特征：只用 Win 作修饰键。
    /// 实测 Win+W / Win+A / Win+F1 全部返回 1409，而 Win+F13 可用 ——
    /// 说明不是 MOD_WIN 被系统性禁用，而是具体按键被逐个预留。
    /// 给用户的可操作建议是：再叠加一个 Ctrl 就能绕开预留。
    /// </summary>
    public static string? SuggestIfReserved(HotkeySpec spec) =>
        spec.Modifiers == HotkeyModifiers.Win
            ? "只带 Win 键的组合在 Windows 11 上大多被系统预留。叠加一个 Ctrl 通常就能绕开，例如 " +
              new HotkeySpec(spec.Modifiers | HotkeyModifiers.Control, spec.VirtualKey, spec.KeyName)
            : null;
}
