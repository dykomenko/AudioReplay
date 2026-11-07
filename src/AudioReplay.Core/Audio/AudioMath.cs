namespace AudioReplay.Core.Audio;

internal static class AudioMath
{
    /// <summary>
    /// Samples within +-NoiseFloor (about -84 dBFS) count as silence. Idle audio sessions often deliver 1-LSB
    /// dither; without this gate such apps would get phantom, effectively silent tracks.
    /// </summary>
    public const short NoiseFloor = 2;

    public static bool HasSignal(ReadOnlySpan<short> samples) =>
        samples.IndexOfAnyExceptInRange((short)-NoiseFloor, NoiseFloor) >= 0;

    /// <summary>dest += src with 16-bit saturation.</summary>
    public static void AddSaturated(Span<short> dest, ReadOnlySpan<short> src)
    {
        for (var i = 0; i < src.Length; i++)
        {
            var v = dest[i] + src[i];
            dest[i] = (short)(v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : v);
        }
    }

    public static void MixInto(Span<int> accumulator, ReadOnlySpan<short> src)
    {
        for (var i = 0; i < src.Length; i++) accumulator[i] += src[i];
    }

    public static void ClampTo(ReadOnlySpan<int> accumulator, Span<short> dest)
    {
        for (var i = 0; i < dest.Length; i++)
        {
            var v = accumulator[i];
            dest[i] = (short)(v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : v);
        }
    }
}
