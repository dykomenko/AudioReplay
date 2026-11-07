using System.Runtime.InteropServices;
using AudioReplay.Core.Audio;
using AudioReplay.Core.Interop;

namespace AudioReplay.Core.Capture;

/// <summary>
/// Captures the audio of one process (and its child processes) through the documented WASAPI
/// "process loopback" virtual device. The target process is identified only by its PID: no handle to it is
/// opened, nothing is injected and nothing is hooked - the Windows audio engine does the work.
/// </summary>
internal sealed class ProcessLoopbackReader : IPacketSource
{
    private const string VirtualDevicePath = "VAD\\Process_Loopback";
    private const ushort VtBlob = 65;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint StreamFlagsEventCallback = 0x00040000;
    private const int AudclntSBufferEmpty = 0x08890001;

    private static Guid _iidAudioClient = typeof(IAudioClient).GUID;
    private static Guid _iidCaptureClient = typeof(IAudioCaptureClient).GUID;

    private readonly AutoResetEvent _event = new(false);
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;

    private ProcessLoopbackReader()
    {
    }

    public WaitHandle Signal => _event;

    /// <summary>Throws <see cref="COMException"/> / <see cref="TimeoutException"/> if the process cannot be captured.</summary>
    public static ProcessLoopbackReader Create(uint processId)
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Process loopback interop requires a 64-bit process.");

        var reader = new ProcessLoopbackReader();
        try
        {
            reader.Open(processId);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public void Start() => Check(_client!.Start());

    public void Stop() => _client?.Stop();

    public void Drain(PacketHandler handler)
    {
        while (true)
        {
            Check(_capture!.GetNextPacketSize(out var next));
            if (next == 0) return;

            var hr = _capture.GetBuffer(out var data, out var frames, out var flags, out _, out var qpc);
            if (hr == AudclntSBufferEmpty) return;
            Check(hr);

            try
            {
                handler(data, (int)frames, (PacketFlags)flags, (long)qpc);
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    public void Dispose()
    {
        try { _client?.Stop(); } catch { /* already gone */ }
        if (_capture is not null) Marshal.ReleaseComObject(_capture);
        if (_client is not null) Marshal.ReleaseComObject(_client);
        _capture = null;
        _client = null;
        _event.Dispose();
    }

    private void Open(uint processId)
    {
        _client = Activate(processId);

        var format = new WaveFormatEx
        {
            FormatTag = 1, // WAVE_FORMAT_PCM
            Channels = AudioFormat.Channels,
            SamplesPerSec = AudioFormat.SampleRate,
            BitsPerSample = AudioFormat.BitsPerSample,
            BlockAlign = AudioFormat.BytesPerFrame,
            AvgBytesPerSec = AudioFormat.SampleRate * AudioFormat.BytesPerFrame,
            Size = 0,
        };

        var pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(format, pFormat, false);
            Check(_client.Initialize(0 /* AUDCLNT_SHAREMODE_SHARED */, StreamFlagsLoopback | StreamFlagsEventCallback, 0, 0, pFormat, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(pFormat);
        }

        Check(_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()));
        Check(_client.GetService(ref _iidCaptureClient, out var service));
        _capture = (IAudioCaptureClient)service;
    }

    private static IAudioClient Activate(uint processId)
    {
        var args = new AudioClientActivationParams
        {
            ActivationType = 1,        // process loopback
            TargetProcessId = processId,
            ProcessLoopbackMode = 0,   // include target process tree
        };

        var pArgs = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        // PROPVARIANT (x64): vt@0, BLOB{cbSize@8, pBlobData@16}, 24 bytes total.
        var pVariant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.StructureToPtr(args, pArgs, false);
            for (var i = 0; i < 24; i += 8) Marshal.WriteInt64(pVariant, i, 0);
            Marshal.WriteInt16(pVariant, 0, (short)VtBlob);
            Marshal.WriteInt32(pVariant, 8, Marshal.SizeOf<AudioClientActivationParams>());
            Marshal.WriteIntPtr(pVariant, 16, pArgs);

            var handler = new ActivationHandler();
            NativeMethods.ActivateAudioInterfaceAsync(VirtualDevicePath, ref _iidAudioClient, pVariant, handler, out var operation);

            if (!handler.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException($"Audio interface activation for PID {processId} timed out.");

            operation.GetActivateResult(out var hr, out var activated);
            Marshal.ThrowExceptionForHR(hr);
            return (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(pVariant);
            Marshal.FreeHGlobal(pArgs);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
