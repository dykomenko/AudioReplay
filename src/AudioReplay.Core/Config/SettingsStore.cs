using System.Text.Json;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.Core.Config;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load(string path)
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json is unreadable, using defaults: {ex.Message}");
            TryBackupCorrupt(path);
            settings = new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>Writes atomically (temp file + replace) so a crash cannot leave a half-written file.</summary>
    public static void Save(AppSettings settings, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryBackupCorrupt(string path)
    {
        try
        {
            if (File.Exists(path)) File.Move(path, path + ".bad", overwrite: true);
        }
        catch
        {
            // best effort
        }
    }
}
