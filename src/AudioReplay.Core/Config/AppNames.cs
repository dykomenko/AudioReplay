namespace AudioReplay.Core.Config;

/// <summary>Normalisation of application identifiers ("Chrome.EXE" -> "chrome").</summary>
public static class AppNames
{
    /// <summary>Lower-case, no ".exe" suffix, no surrounding whitespace.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var n = name.Trim().ToLowerInvariant();
        if (n.EndsWith(".exe", StringComparison.Ordinal)) n = n[..^4];
        return n;
    }
}
