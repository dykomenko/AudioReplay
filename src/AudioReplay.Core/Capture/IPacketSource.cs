namespace AudioReplay.Core.Capture;

[Flags]
internal enum PacketFlags
{
    None = 0,
    Discontinuity = 0x1,
    Silent = 0x2,
    TimestampError = 0x4,
}

/// <param name="Data">Pointer to 16-bit stereo 48 kHz samples; valid only during the callback.</param>
/// <param name="Frames">Number of sample frames.</param>
/// <param name="Qpc100ns">Performance-counter time of the first frame, in 100 ns units.</param>
internal delegate void PacketHandler(IntPtr data, int frames, PacketFlags flags, long qpc100ns);

/// <summary>A WASAPI capture stream already configured to deliver 48 kHz / 16-bit / stereo.</summary>
internal interface IPacketSource : IDisposable
{
    /// <summary>Signalled when the audio engine has data; waiting on it with a timeout is enough.</summary>
    WaitHandle Signal { get; }

    void Start();

    void Stop();

    /// <summary>Hands every currently available packet to <paramref name="handler"/>.</summary>
    void Drain(PacketHandler handler);
}
