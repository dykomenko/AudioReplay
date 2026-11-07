using AudioReplay.Core.Audio;

namespace AudioReplay.Core.Output;

public static class FileNaming
{
    public const string MixFileName = "mix.wav";

    /// <summary>"02_chrome.wav": the permanent slot number keeps one app on the same position forever.</summary>
    public static string TrackFileName(TrackInfo track) => $"{track.Slot:00}_{Sanitize(track.Id)}.wav";

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.');
        if (clean.Length == 0) clean = "track";
        return clean.Length > 40 ? clean[..40] : clean;
    }

    /// <summary>Creates <paramref name="parent"/>\<paramref name="name"/>, adding "_2", "_3"... if it exists.</summary>
    public static string CreateUniqueFolder(string parent, string name)
    {
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, name);
        for (var i = 2; Directory.Exists(path); i++) path = Path.Combine(parent, $"{name}_{i}");
        Directory.CreateDirectory(path);
        return path;
    }
}
