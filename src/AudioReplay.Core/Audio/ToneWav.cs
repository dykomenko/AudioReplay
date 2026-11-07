using System.Buffers.Binary;

namespace AudioReplay.Core.Audio;

/// <summary>Builds tiny in-memory WAV files (a sequence of sine beeps) for the feedback sounds.</summary>
public static class ToneWav
{
    private const int Rate = 44_100;
    private const double Amplitude = 0.22;
    private const int FadeMilliseconds = 6;

    /// <summary>16-bit mono PCM. Each tone is (frequency in Hz, duration in ms); short fades avoid clicks.</summary>
    public static byte[] Create(params (double Hz, int Milliseconds)[] tones)
    {
        var total = tones.Sum(t => t.Milliseconds * Rate / 1000);
        var data = new byte[total * 2];
        var pos = 0;

        foreach (var (hz, ms) in tones)
        {
            var frames = ms * Rate / 1000;
            var fade = Math.Min(FadeMilliseconds * Rate / 1000, frames / 2);
            for (var i = 0; i < frames; i++)
            {
                var envelope = i < fade ? (double)i / fade : i >= frames - fade ? (double)(frames - 1 - i) / fade : 1.0;
                var sample = (short)(Math.Sin(2 * Math.PI * hz * i / Rate) * Amplitude * envelope * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(pos * 2), sample);
                pos++;
            }
        }

        var wav = new byte[44 + data.Length];
        "RIFF"u8.CopyTo(wav.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(36 + data.Length));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), Rate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), Rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)data.Length);
        data.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
