namespace AudioReplay.Core.Audio;

/// <summary>The single internal format: 48 kHz, 16-bit, stereo, interleaved. WASAPI converts to it for us.</summary>
public static class AudioFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BytesPerFrame = Channels * BitsPerSample / 8;

    /// <summary>Buffers are stored in 100 ms chunks; a silent chunk costs no memory.</summary>
    public const int ChunkFrames = SampleRate / 10;

    public const int ChunkSamples = ChunkFrames * Channels;
}
