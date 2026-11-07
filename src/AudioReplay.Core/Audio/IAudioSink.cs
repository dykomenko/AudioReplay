namespace AudioReplay.Core.Audio;

public interface IAudioSink
{
    /// <summary>
    /// Delivers interleaved 16-bit stereo samples that start at <paramref name="startFrame"/> on the
    /// <see cref="FrameClock"/> timeline. Called from capture threads.
    /// </summary>
    void Write(TrackInfo track, long startFrame, ReadOnlySpan<short> samples);
}
