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

    private WavFileWriter(FileStream stream, long existingFrames)
    {
        _stream = stream;
        _framesEnd = existingFrames;
    }

    public string Path => _stream.Name;

    /// <summary>Number of frames the file holds so far (including what was there before an append).</summary>
    public long FramesWritten => _framesEnd;

    /// <summary>
    /// Opens a WAV previously produced by this class so more audio can be written after its end.
    /// Throws (IOException, InvalidDataException, ...) without side effects if it cannot be extended,
    /// e.g. the file is held open by an editor.
    /// </summary>
    public static WavFileWriter OpenForAppend(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        try
        {
            Span<byte> h = stackalloc byte[HeaderSize];
            var valid = stream.Read(h) == HeaderSize
                && h[..4].SequenceEqual("RIFF"u8)
                && h[8..12].SequenceEqual("WAVE"u8)
                && h[36..40].SequenceEqual("data"u8)
                && BinaryPrimitives.ReadUInt16LittleEndian(h[22..]) == AudioFormat.Channels
                && BinaryPrimitives.ReadUInt32LittleEndian(h[24..]) == AudioFormat.SampleRate
                && BinaryPrimitives.ReadUInt16LittleEndian(h[34..]) == AudioFormat.BitsPerSample;
            if (!valid) throw new InvalidDataException($"'{path}' is not a 48 kHz/16-bit/stereo WAV written by AudioReplay.");

            var dataBytes = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(h[40..]), stream.Length - HeaderSize);
            return new WavFileWriter(stream, dataBytes / AudioFormat.BytesPerFrame);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

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
