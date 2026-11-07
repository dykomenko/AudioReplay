using System.Runtime.InteropServices;
using AudioReplay.Core.Input;

namespace AudioReplay.App.Platform;

/// <summary>
/// Global hotkeys via the documented RegisterHotKey API on a message-only window. No keyboard hooks
/// (SetWindowsHookEx) and no polling of key state - only the keys the user explicitly registers are delivered.
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Dictionary<int, (Hotkey Key, Action Handler)> _registered = [];
    private bool _suspended;

    public HotkeyManager() => CreateHandle(new CreateParams { Parent = HwndMessage });

    /// <summary>Registers (or replaces) a hotkey. Returns false if another application already owns the combination.</summary>
    public bool Register(int id, Hotkey hotkey, Action handler)
    {
        Unregister(id);
        if (!hotkey.IsValid) return false;

        _registered[id] = (hotkey, handler);
        return _suspended || Native(id, hotkey);
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id) && !_suspended) UnregisterHotKey(Handle, id);
    }

    /// <summary>Releases all combinations (used while the user is recording a new hotkey in the settings window).</summary>
    public void Suspend()
    {
        if (_suspended) return;
        foreach (var id in _registered.Keys) UnregisterHotKey(Handle, id);
        _suspended = true;
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        foreach (var (id, entry) in _registered) Native(id, entry.Key);
    }

    public void Dispose()
    {
        if (!_suspended)
        {
            foreach (var id in _registered.Keys) UnregisterHotKey(Handle, id);
        }

        _registered.Clear();
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && !_suspended && _registered.TryGetValue((int)m.WParam, out var entry))
        {
            entry.Handler();
            return;
        }

        base.WndProc(ref m);
    }

    private bool Native(int id, Hotkey hotkey) =>
        RegisterHotKey(Handle, id, (uint)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
