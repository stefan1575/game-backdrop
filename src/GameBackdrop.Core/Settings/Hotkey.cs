namespace GameBackdrop.Core.Settings;

/// <summary>The modifier keys of a hotkey. The values are Win32's MOD_ flags, but this is plain data.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// A global hotkey: modifiers plus one key, as a Win32 virtual-key code. Written in the settings
/// file as text such as <c>Ctrl+Alt+B</c>: modifiers (Ctrl or Control, Alt, Shift, Win), then a
/// letter, a digit or F1–F24, joined by <c>+</c>, any case.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>Ctrl+Alt+B: what toggles the backdrop when the settings name no hotkey.</summary>
    public static Hotkey Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 'B');

    private const uint VK_F1 = 0x70;

    /// <summary>
    /// Read a hotkey's written form. It needs one key and a modifier other than Shift alone, so it
    /// can't take a plain key or a capital letter away from every other app.
    /// </summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        uint? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var modifier = ModifierNamed(part);
            if (modifier != HotkeyModifiers.None)
            {
                if ((modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }
            if (key is not null || KeyNamed(part) is not { } named) return false;
            key = named;
        }

        if (key is null || (modifiers & ~HotkeyModifiers.Shift) == HotkeyModifiers.None) return false;
        hotkey = new Hotkey(modifiers, key.Value);
        return true;
    }

    /// <summary>
    /// The hotkey the settings name (<see cref="AppSettings.Hotkey"/>): <see cref="Default"/> when
    /// they name none, and also when they name one that can't be read, with
    /// <paramref name="understood"/> false so the app can say so.
    /// </summary>
    public static Hotkey FromSettings(string? text, out bool understood)
    {
        understood = true;
        if (text is null) return Default;
        if (TryParse(text, out var hotkey)) return hotkey;
        understood = false;
        return Default;
    }

    private static HotkeyModifiers ModifierNamed(string name) => name.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static uint? KeyNamed(string name)
    {
        var upper = name.ToUpperInvariant();
        if (upper.Length == 1 && upper[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) return upper[0];
        if (upper.Length > 1 && upper[0] == 'F' && int.TryParse(upper.AsSpan(1), out var n) && n is >= 1 and <= 24
            && upper[1] != '0')
            return VK_F1 + (uint)(n - 1);
        return null;
    }

    /// <summary>The written form, modifiers in a fixed order: <c>Ctrl+Alt+Shift+Win+Key</c>.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(VirtualKey >= VK_F1 && VirtualKey < VK_F1 + 24 ? $"F{VirtualKey - VK_F1 + 1}" : ((char)VirtualKey).ToString());
        return string.Join("+", parts);
    }
}
