using System.Runtime.InteropServices;

namespace AudioReplay.Core.Interop;

// Minimal COM declarations for WASAPI per-process loopback capture (Windows 10 2004+ / Windows 11).
// Only documented public interfaces from audioclient.h / mmdeviceapi.h are used.

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig] int GetBufferSize(out uint numBufferFrames);

    [PreserveSig] int GetStreamLatency(out long latency);

    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);

    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);

    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

    [PreserveSig] int Start();

    [PreserveSig] int Stop();

    [PreserveSig] int Reset();

    [PreserveSig] int SetEventHandle(IntPtr eventHandle);

    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig] int ReleaseBuffer(uint framesRead);

    [PreserveSig] int GetNextPacketSize(out uint framesInNextPacket);
}

[ComImport]
[Guid("41D949AB-9862-444a-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

/// <summary>Marker: tells COM the completion handler may be called from any thread.</summary>
[ComImport]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;        // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
    public uint TargetProcessId;
    public int ProcessLoopbackMode;   // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;
}

internal static class NativeMethods
{
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    public static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);
}

internal sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    private readonly ManualResetEventSlim _done = new(false);

    public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => _done.Set();

    public bool Wait(TimeSpan timeout) => _done.Wait(timeout);
}
