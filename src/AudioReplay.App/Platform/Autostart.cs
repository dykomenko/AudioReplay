using Microsoft.Win32;

namespace AudioReplay.App.Platform;

/// <summary>"Start with Windows" through the per-user Run key (no admin rights, no scheduled task, no service).</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AudioReplay";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            if (enabled && Environment.ProcessPath is { } exe)
            {
                var desired = $"\"{exe}\"";
                if (!string.Equals(key.GetValue(ValueName) as string, desired, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, desired);
            }
            else if (!enabled && key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Non-critical: the setting just will not take effect.
        }
    }
}
