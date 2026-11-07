using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AudioReplay.Core.Audio;

namespace AudioReplay.Core.Output;

/// <summary>
/// Writes a 48 kHz / 16-bit / stereo PCM WAV at arbitrary frame positions. Seeking forward leaves a
/// zero-filled gap, which is how silence between sounds (and a track's late start) is represented.
/// </summary>
public sealed class WavFileWriter : IDisposable
{
    public const int HeaderSize = 44;

    /// <summary>RIFF stores sizes in 32 bits; about 5.8 hours of this format.</summary>
    public const long MaxFrames = (uint.MaxValue - HeaderSize) / AudioFormat.BytesPerFrame;

    private readonly FileStream _stream;
    private long _framesEnd;
    private bool _completed;

    public WavFileWriter(string path)
    {
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        WriteHeader(0);
    }

    public string Path => _stream.Name;

    public void WriteAtFrame(long frameIndex, ReadOnlySpan<short> samples)
    {
        var frames = samples.Length / AudioFormat.Channels;
        if (frames == 0) return;
        if (frameIndex + frames > MaxFrames) throw new IOException("WAV size limit reached.");

        var position = HeaderSize + frameIndex * AudioFormat.BytesPerFrame;
        if (_stream.Position != position) _stream.Seek(position, SeekOrigin.Begin);

        _stream.Write(MemoryMarshal.AsBytes(samples));
        _framesEnd = Math.Max(_framesEnd, frameIndex + frames);
    }

    /// <summary>Pads/truncates the data section to exactly <paramref name="totalFrames"/>, fixes the header and closes.</summary>
    public void Complete(long totalFrames)
    {
        if (_completed) return;
        _completed = true;

        try
        {
            totalFrames = Math.Min(totalFrames, MaxFrames);
            _stream.SetLength(HeaderSize + totalFrames * AudioFormat.BytesPerFrame);
            WriteHeader(totalFrames);
            _stream.Flush(true);
        }
        finally
        {
            _stream.Dispose();
        }
    }

    public void Dispose() => Complete(_framesEnd);

    private void WriteHeader(long frames)
    {
        var dataBytes = (uint)(frames * AudioFormat.BytesPerFrame);
        Span<byte> h = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(h[0..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], AudioFormat.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], AudioFormat.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], AudioFormat.SampleRate * AudioFormat.BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], AudioFormat.BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], AudioFormat.BitsPerSample);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], dataBytes);

        var restore = _stream.Position;
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.Write(h);
        if (restore > HeaderSize) _stream.Seek(restore, SeekOrigin.Begin);
    }
}
