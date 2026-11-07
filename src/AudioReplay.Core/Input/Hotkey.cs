namespace AudioReplay.Core.Input;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global hotkey: modifier mask + Win32 virtual-key code. Serialised as "Ctrl+Alt+F10".</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> NameToVk = BuildNames();

    private static readonly Dictionary<uint, string> VkToName =
        NameToVk.ToDictionary(kv => kv.Value, kv => kv.Key);

    public bool IsValid => VirtualKey != 0;

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var mods = HotkeyModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Control; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win" or "windows": mods |= HotkeyModifiers.Win; break;
                default: return false;
            }
        }

        var keyToken = parts[^1];
        if (!NameToVk.TryGetValue(keyToken.ToLowerInvariant(), out var vk)) return false;

        hotkey = new Hotkey(mods, vk);
        return true;
    }

    public static string NameOf(uint virtualKey) =>
        VkToName.TryGetValue(virtualKey, out var name) ? name : $"0x{virtualKey:X2}";

    public static bool IsModifierKey(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(VirtualKey == 0 ? "None" : VkToDisplay(VirtualKey));
        return string.Join('+', parts);
    }

    // Names are stored lower-case for lookup; display them capitalised.
    private static string VkToDisplay(uint vk)
    {
        var n = NameOf(vk);
        return n.Length <= 1 ? n.ToUpperInvariant() : char.ToUpperInvariant(n[0]) + n[1..];
    }

    private static Dictionary<string, uint> BuildNames()
    {
        var d = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var i = 1; i <= 24; i++) d[$"f{i}"] = (uint)(0x6F + i);
        for (var c = 'a'; c <= 'z'; c++) d[c.ToString()] = (uint)(c - 'a' + 0x41);
        for (var c = '0'; c <= '9'; c++) d[c.ToString()] = (uint)(c - '0' + 0x30);
        for (var i = 0; i <= 9; i++) d[$"num{i}"] = (uint)(0x60 + i);

        d["space"] = 0x20; d["enter"] = 0x0D; d["tab"] = 0x09; d["esc"] = 0x1B; d["backspace"] = 0x08;
        d["insert"] = 0x2D; d["delete"] = 0x2E; d["home"] = 0x24; d["end"] = 0x23;
        d["pageup"] = 0x21; d["pagedown"] = 0x22;
        d["left"] = 0x25; d["up"] = 0x26; d["right"] = 0x27; d["down"] = 0x28;
        d["printscreen"] = 0x2C; d["scrolllock"] = 0x91; d["pause"] = 0x13;
        d["nummul"] = 0x6A; d["numadd"] = 0x6B; d["numsub"] = 0x6D; d["numdec"] = 0x6E; d["numdiv"] = 0x6F;
        d["semicolon"] = 0xBA; d["equals"] = 0xBB; d["comma"] = 0xBC; d["minus"] = 0xBD; d["period"] = 0xBE;
        d["slash"] = 0xBF; d["backtick"] = 0xC0; d["lbracket"] = 0xDB; d["backslash"] = 0xDC;
        d["rbracket"] = 0xDD; d["quote"] = 0xDE;
        return d;
    }
}
