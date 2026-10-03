using System.Text;

namespace ClipboardTool.Domain.Hotkeys;

/// <summary>
/// accel 协议字符串 ↔ 组合键的编解码（F30/F31；码表按 legacy hotkeys.rs 与 tauri Shortcut
/// 解析口径）：设置存档存串（Control+Shift+V），注册与捕获要组合键，两向都要走通。
/// 修饰键别名大小写不敏感（Ctrl/Control/CommandOrControl → Control；Super/Meta → Win）；
/// 主键支持字母/数字/F1–F24 与 Space/Enter/Esc/Tab/Backspace/Delete/Insert/Home/End/
/// PageUp/PageDown/方向键。规范形（持久化与展示的同一串形）= Control+Alt+Shift+Win 顺序。
/// </summary>
public static class AccelCodec
{
    /// <summary>解析 accel → 组合键；不可解析（未知词、缺主键、尾随分隔符）返回 null。</summary>
    public static HotkeyCombo? Parse(string accel)
    {
        if (string.IsNullOrWhiteSpace(accel))
        {
            return null;
        }
        var modifiers = HotkeyModifiers.None;
        uint? virtualKey = null;
        foreach (var raw in accel.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                return null; // "Control+" 之类：尾随分隔符视为坏档
            }
            switch (part.ToLowerInvariant())
            {
                case "control" or "ctrl" or "commandorcontrol":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "super" or "meta" or "win":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }
            if (virtualKey is not null)
            {
                return null; // 两个主键：不可解析
            }
            virtualKey = ParseMainKey(part);
            if (virtualKey is null)
            {
                return null;
            }
        }
        return virtualKey is { } vk ? new HotkeyCombo(modifiers, vk) : null;
    }

    /// <summary>主键码表：accel 主键名 → 虚拟键码（认不出返回 null）。</summary>
    private static uint? ParseMainKey(string name) => name.ToUpperInvariant() switch
    {
        "ESC" or "ESCAPE" => 0x1B,
        "ENTER" => 0x0D,
        "SPACE" => 0x20,
        "TAB" => 0x09,
        "BACKSPACE" => 0x08,
        "DELETE" => 0x2E,
        "INSERT" => 0x2D,
        "HOME" => 0x24,
        "END" => 0x23,
        "PAGEUP" => 0x21,
        "PAGEDOWN" => 0x22,
        "UP" => 0x26,
        "DOWN" => 0x28,
        "LEFT" => 0x25,
        "RIGHT" => 0x27,
        _ when name.Length == 1 && name[0] is >= 'A' and <= 'Z' => name[0],           // 字母 A–Z
        _ when name.Length == 1 && name[0] is >= '0' and <= '9' => name[0],           // 数字 0–9
        _ when name.Length >= 2 && name[0] is 'F' or 'f'
            && uint.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24 => 0x70 + fn - 1, // F1–F24
        _ => null,
    };

    /// <summary>组合键 → 规范 accel 串（Control 命名、修饰顺序 Control+Alt+Shift+Win，主键殿后）。</summary>
    public static string Encode(HotkeyCombo combo)
    {
        var sb = new StringBuilder();
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Control)) Append("Control");
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Alt)) Append("Alt");
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Shift)) Append("Shift");
        if (combo.Modifiers.HasFlag(HotkeyModifiers.Win)) Append("Win");
        if (sb.Length > 0)
        {
            sb.Append('+');
        }
        sb.Append(MainKeyName(combo.VirtualKey));
        return sb.ToString();

        void Append(string modifier)
        {
            if (sb.Length > 0)
            {
                sb.Append('+');
            }
            sb.Append(modifier);
        }
    }

    /// <summary>虚拟键 → 规范主键名（Encode 的反向；未知键按 VK_ 码兜底保证不产出空串）。</summary>
    public static string MainKeyName(uint vk) => vk switch
    {
        0x1B => "Esc",
        0x0D => "Enter",
        0x20 => "Space",
        0x09 => "Tab",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => $"VK_{vk:X2}",
    };

    /// <summary>
    /// accel → 展示文案（legacy hotkeys.rs format_shortcut；Control+Shift+V → Ctrl + Shift + V）。
    /// 托盘菜单、捕获覆盖层、更换快捷键的回报三处共用。空串按默认键展示（存档契约同口径）。
    /// </summary>
    public static string FormatDisplay(string accel)
    {
        var text = accel.Length == 0 ? Settings.SettingsRules.DefaultShortcut : accel;
        return string.Join(" + ", text.Split('+').Select(part => part switch
        {
            "Control" or "CommandOrControl" => "Ctrl",
            "Super" or "Meta" => "Win",
            _ => part,
        }));
    }
}
