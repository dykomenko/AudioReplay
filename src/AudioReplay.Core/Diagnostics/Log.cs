using System.Text;

namespace AudioReplay.Core.Diagnostics;

/// <summary>Tiny thread-safe file logger. The app has no console, so this is the only diagnostics channel.</summary>
public static class Log
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();
    private static string? _path;
    private static readonly bool DebugEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AUDIOREPLAY_DEBUG"));

    public static void Init(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    public static void Debug(string message)
    {
        if (DebugEnabled) Write("DEBUG", message);
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Ignore I/O failures.
            }
        }
    }
}
