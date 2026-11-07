using AudioReplay.Core.Input;

namespace AudioReplay.App.UI;

/// <summary>A read-only text box that records the next key combination pressed while it has focus.</summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey _value;

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        TextAlign = HorizontalAlignment.Center;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            Text = value.IsValid ? value.ToString() : string.Empty;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Focused) return base.ProcessCmdKey(ref msg, keyData);

        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;

        // Plain Tab / Esc keep their normal meaning so the user can leave the box.
        if (modifiers == Keys.None && key is Keys.Tab or Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);

        // Modifier pressed on its own: wait for the real key.
        if (Hotkey.IsModifierKey((uint)key)) return true;

        var mods = HotkeyModifiers.None;
        if (modifiers.HasFlag(Keys.Control)) mods |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(Keys.Alt)) mods |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(Keys.Shift)) mods |= HotkeyModifiers.Shift;

        Value = new Hotkey(mods, (uint)key);
        return true;
    }
}
