namespace AudioReplay.Core.Config;

/// <summary>Well-known locations. The data directory can be redirected with AUDIOREPLAY_DATA_DIR (tests, portable use).</summary>
public static class AppPaths
{
    public const string AppName = "AudioReplay";

    public static string DataDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("AUDIOREPLAY_DATA_DIR");
            var dir = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Empty normally. When AUDIOREPLAY_DATA_DIR is set (tests, a second isolated copy) it is a short hash of that
    /// path, so the single-instance mutex and the control pipe do not collide with the user's regular instance.
    /// </summary>
    public static string InstanceSuffix
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("AUDIOREPLAY_DATA_DIR");
            if (string.IsNullOrWhiteSpace(overrideDir)) return string.Empty;

            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(overrideDir).ToLowerInvariant()));
            return "." + Convert.ToHexString(hash)[..8];
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string TracksFile => Path.Combine(DataDirectory, "tracks.json");

    public static string LogFile => Path.Combine(DataDirectory, "audioreplay.log");

    public static string DefaultSaveDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), AppName);
}
