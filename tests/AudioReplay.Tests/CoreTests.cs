using AudioReplay.Core.Audio;
using AudioReplay.Core.Capture;
using AudioReplay.Core.Config;
using AudioReplay.Core.Input;
using AudioReplay.Core.Output;
using Xunit;

namespace AudioReplay.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F10")]
    [InlineData("Shift+A")]
    [InlineData("F9")]
    [InlineData("Ctrl+Shift+Win+Num5")]
    public void RoundTrips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(text, hotkey.ToString());
    }

    [Fact]
    public void ParsesModifiersAndKey()
    {
        Assert.True(Hotkey.TryParse("control+alt+f10", out var h));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, h.Modifiers);
        Assert.Equal(0x79u, h.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Foo+F1")]
    public void RejectsGarbage(string text) => Assert.False(Hotkey.TryParse(text, out _));
}

public class TrackRingTests
{
    private static readonly TrackInfo Track = new("test", 1);

    private static short[] Tone(int frames, short value)
    {
        var s = new short[frames * 2];
        Array.Fill(s, value);
        return s;
    }

    [Fact]
    public void SilenceAllocatesNothing()
    {
        var ring = new TrackRing(Track, 100);
        ring.Write(1000, new short[AudioFormat.ChunkFrames * 2 * 3]);

        Assert.All(ring.SnapshotRange(0, 20), c => Assert.Null(c));
    }

    [Fact]
    public void OneBitDitherCountsAsSilence()
    {
        var ring = new TrackRing(Track, 100);
        ring.Write(0, Tone(1000, 2));
        ring.Write(0, Tone(1000, -2));
        Assert.Null(ring.SnapshotRange(0, 0)[0]);

        ring.Write(0, Tone(10, 3)); // just above the noise floor is kept
        Assert.NotNull(ring.SnapshotRange(0, 0)[0]);
    }

    [Fact]
    public void WriteAcrossChunkBoundaryIsSplitCorrectly()
    {
        var ring = new TrackRing(Track, 100);
        var start = AudioFormat.ChunkFrames - 10;
        ring.Write(start, Tone(30, 5));

        var chunks = ring.SnapshotRange(0, 1);
        Assert.NotNull(chunks[0]);
        Assert.NotNull(chunks[1]);
        Assert.Equal(5, chunks[0]![(AudioFormat.ChunkFrames - 1) * 2]);
        Assert.Equal(0, chunks[0]![(AudioFormat.ChunkFrames - 11) * 2]);
        Assert.Equal(5, chunks[1]![19 * 2]);
        Assert.Equal(0, chunks[1]![20 * 2]);
    }

    [Fact]
    public void OverlappingWritesAreSummedWithSaturation()
    {
        var ring = new TrackRing(Track, 100);
        ring.Write(0, Tone(10, 20000));
        ring.Write(0, Tone(10, 20000));

        Assert.Equal(short.MaxValue, ring.SnapshotRange(0, 0)[0]![0]);
    }

    [Fact]
    public void OldChunksFallOutOfTheWindow()
    {
        var ring = new TrackRing(Track, 10);
        ring.Write(0, Tone(100, 7));
        ring.Write(10L * AudioFormat.ChunkFrames, Tone(100, 9)); // same slot, newer chunk

        Assert.Null(ring.SnapshotRange(0, 0)[0]);
        Assert.Equal(9, ring.SnapshotRange(10, 10)[0]![0]);
    }

    [Fact]
    public void TrimReleasesStaleChunks()
    {
        var ring = new TrackRing(Track, 10);
        ring.Write(0, Tone(100, 7));
        ring.Trim(1000);

        Assert.False(ring.TryCopyChunk(0, new short[AudioFormat.ChunkSamples]));
    }

    [Fact]
    public void ResizeKeepsRecentData()
    {
        var ring = new TrackRing(Track, 10);
        ring.Write(5L * AudioFormat.ChunkFrames, Tone(100, 3));
        ring.Resize(50);

        Assert.Equal(3, ring.SnapshotRange(5, 5)[0]![0]);
    }
}

public class WavFileWriterTests
{
    [Fact]
    public void ProducesValidHeaderAndPadsGaps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-test-{Guid.NewGuid():N}.wav");
        try
        {
            using (var w = new WavFileWriter(path))
            {
                w.WriteAtFrame(100, [1, 1, 2, 2]);
                w.Complete(1000);
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + 1000 * 4, bytes.Length);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(1000u * 4, BitConverter.ToUInt32(bytes, 40));
            Assert.Equal(48000u, BitConverter.ToUInt32(bytes, 24));
            Assert.Equal((short)1, BitConverter.ToInt16(bytes, 44 + 100 * 4));
            Assert.Equal((short)2, BitConverter.ToInt16(bytes, 44 + 101 * 4));
            Assert.Equal((short)0, BitConverter.ToInt16(bytes, 44 + 99 * 4)); // gap is silence
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class CapturePlannerTests
{
    private static ProcInfo P(uint pid, uint parent, string exe) => new(pid, parent, exe);

    private static Dictionary<uint, ProcInfo> Procs(params ProcInfo[] p) => p.ToDictionary(x => x.Pid);

    [Fact]
    public void GroupsByExecutableAndSkipsInactive()
    {
        var procs = Procs(P(10, 1, "Game.exe"), P(20, 1, "chrome.exe"), P(30, 1, "idle.exe"));
        var plan = CapturePlanner.Plan(
            [new(10, true), new(20, true), new(30, false)], procs, _ => false, selfPid: 999);

        Assert.Equal(["chrome", "game"], plan.Targets.Select(t => t.TrackId).Order().ToArray());
    }

    [Fact]
    public void ChildOfSameExeIsCoveredByParent()
    {
        var procs = Procs(P(1, 0, "explorer.exe"), P(10, 1, "chrome.exe"), P(11, 10, "crashpad.exe"), P(12, 11, "chrome.exe"));
        var plan = CapturePlanner.Plan([new(10, true), new(12, true)], procs, _ => false, 999);

        var target = Assert.Single(plan.Targets);
        Assert.Equal(10u, target.Pid);
        Assert.Contains(12u, plan.Skipped);
    }

    [Fact]
    public void ExcludedAndSelfAreNeverCaptured()
    {
        var procs = Procs(P(10, 1, "Secret.exe"), P(20, 1, "AudioReplay.exe"));
        var plan = CapturePlanner.Plan([new(10, true), new(20, true)], procs, id => id == "secret", selfPid: 20);

        Assert.Empty(plan.Targets);
        Assert.Contains(10u, plan.Skipped);
    }

    [Fact]
    public void ParentCycleDoesNotHang()
    {
        var procs = Procs(P(10, 11, "a.exe"), P(11, 10, "b.exe"));
        var plan = CapturePlanner.Plan([new(10, true)], procs, _ => false, 999);

        Assert.Single(plan.Targets);
    }
}

public class TrackRegistryTests
{
    [Fact]
    public void SlotsArePermanentAcrossRestarts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ar-tracks-{Guid.NewGuid():N}.json");
        try
        {
            var first = new TrackRegistry(path);
            var chrome = first.Get("chrome");
            var game = first.Get("game");

            var second = new TrackRegistry(path);
            Assert.Equal(chrome.Slot, second.Get("chrome").Slot);
            Assert.Equal(game.Slot, second.Get("game").Slot);
            Assert.Equal(1, second.Get(TrackRegistry.MicrophoneId).Slot);
            Assert.NotEqual(chrome.Slot, game.Slot);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SettingsTests
{
    [Fact]
    public void NormalizeClampsAndCleans()
    {
        var s = new AppSettings
        {
            ReplaySeconds = 99999,
            ReplayHotkey = "nonsense",
            ExcludedApps = [" Game.EXE ", "game", "", "Other.exe"],
        };
        s.Normalize();

        Assert.Equal(AppSettings.MaxReplaySeconds, s.ReplaySeconds);
        Assert.Equal("Alt+Q", s.ReplayHotkey);
        Assert.Equal(["game", "other"], s.ExcludedApps);
    }
}

public class StreamerModeTests
{
    [Fact]
    public void DefaultsAreOnWithThirtySecondReplay()
    {
        var s = new AppSettings();

        Assert.True(s.StreamerMode);
        Assert.True(s.PlaySound);
        Assert.True(s.ShowNotifications);
        Assert.Equal(30, s.ReplaySeconds);
        Assert.Equal(30, s.MergeWindowSeconds);
        Assert.Contains("obs64", s.StreamerApps);
    }

    [Fact]
    public void ObsProcessIsDetectedCaseInsensitively()
    {
        var running = new[] { new ProcInfo(1, 0, "explorer.exe"), new ProcInfo(2, 1, "OBS64.EXE") };

        Assert.True(StreamingAppDetector.IsAnyRunning(running, new HashSet<string> { "obs64" }));
        Assert.False(StreamingAppDetector.IsAnyRunning(running, new HashSet<string> { "streamlabs desktop" }));
    }

    [Fact]
    public void EmptyStreamerAppListFallsBackToDefaults()
    {
        var s = new AppSettings { StreamerApps = ["", "  "] };
        s.Normalize();

        Assert.Equal(StreamingAppDetector.DefaultApps, s.StreamerApps);
    }

    [Fact]
    public void ToneWavIsAValidShortFile()
    {
        var wav = ToneWav.Create((880, 100), (1175, 100));

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        var dataBytes = BitConverter.ToUInt32(wav, 40);
        Assert.Equal(wav.Length - 44, (int)dataBytes);
        Assert.Equal(2 * 100 * 44100 / 1000 * 2, (int)dataBytes);
        Assert.Contains(wav.Skip(44).Chunk(2).Select(c => BitConverter.ToInt16(c)), v => Math.Abs((int)v) > 1000);
    }
}

public class OutputTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ar-test-" + Guid.NewGuid().ToString("N"));

    private static void WriteTone(TrackStore store, TrackInfo track, long startFrame, int frames, short value)
    {
        var samples = new short[frames * 2];
        Array.Fill(samples, value);
        store.Write(track, startFrame, samples);
    }

    [Fact]
    public async Task ReplayWritesAlignedFilesPerTrackWithStableNames()
    {
        var dir = TempDir();
        try
        {
            var store = new TrackStore(TimeSpan.FromSeconds(30));
            var a = new TrackInfo("alpha", 2);
            var b = new TrackInfo("beta", 5);
            var now = FrameClock.NowFrame;
            WriteTone(store, a, now - 4 * AudioFormat.SampleRate, 4 * AudioFormat.SampleRate, 1000);
            WriteTone(store, b, now - AudioFormat.SampleRate, AudioFormat.SampleRate / 2, 2000);

            var result = await new ReplaySaver(store).SaveAsync(3, dir, writeMix: true);

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.TrackCount);
            var files = Directory.GetFiles(result.Folder!).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(new[] { "02_alpha.wav", "05_beta.wav", "mix.wav" }, files.Select(f => f!).ToArray());

            var lengths = Directory.GetFiles(result.Folder!).Select(f => new FileInfo(f).Length).Distinct().ToArray();
            Assert.Single(lengths); // every track and the mix have identical length
            Assert.InRange(lengths[0], 44 + 3 * 192_000 - 4, 44 + 3 * 192_000 + 4 * 9600);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static short[] ReadSamples(string wav) =>
        File.ReadAllBytes(wav).Skip(44).Chunk(2).Select(c => BitConverter.ToInt16(c)).ToArray();

    [Fact]
    public void WavAppendKeepsOldAudioAndExtends()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a.wav");
        try
        {
            using (var w = new WavFileWriter(path))
            {
                w.WriteAtFrame(0, [7, 7, 7, 7]);
                w.Complete(100);
            }

            using (var w = WavFileWriter.OpenForAppend(path))
            {
                Assert.Equal(100, w.FramesWritten);
                w.WriteAtFrame(150, [9, 9]);
                w.Complete(200);
            }

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + 200 * 4, bytes.Length);
            Assert.Equal(200u * 4, BitConverter.ToUInt32(bytes, 40));
            Assert.Equal((short)7, BitConverter.ToInt16(bytes, 44));
            Assert.Equal((short)0, BitConverter.ToInt16(bytes, 44 + 120 * 4)); // gap between old end and new data
            Assert.Equal((short)9, BitConverter.ToInt16(bytes, 44 + 150 * 4));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SecondSaveWithinWindowIsAppendedToTheSameClip()
    {
        var dir = TempDir();
        try
        {
            var store = new TrackStore(TimeSpan.FromSeconds(30));
            var saver = new ReplaySaver(store);
            var alpha = new TrackInfo("alpha", 2);
            var beta = new TrackInfo("beta", 5);
            var now = FrameClock.NowFrame;
            WriteTone(store, alpha, now - 3 * AudioFormat.SampleRate, 3 * AudioFormat.SampleRate, 1000);

            var first = await saver.SaveAsync(2, dir, writeMix: true, mergeWindowSeconds: 10);
            Assert.True(first.Success, first.Message);
            Assert.False(first.Appended);

            await Task.Delay(1000);
            var t = FrameClock.NowFrame;
            WriteTone(store, alpha, t - AudioFormat.SampleRate, AudioFormat.SampleRate, 1500);
            WriteTone(store, beta, t - AudioFormat.SampleRate / 2, AudioFormat.SampleRate / 2, 2500); // a new app appears

            var second = await saver.SaveAsync(2, dir, writeMix: true, mergeWindowSeconds: 10);

            Assert.True(second.Success, second.Message);
            Assert.True(second.Appended);
            Assert.Equal(first.Folder, second.Folder);
            Assert.Single(Directory.GetDirectories(dir));
            Assert.Equal(["02_alpha.wav", "05_beta.wav", "mix.wav"], Directory.GetFiles(second.Folder!).Select(f => Path.GetFileName(f)!).Order().ToArray());
            Assert.InRange(second.Added.TotalSeconds, 1.0, 1.6);
            Assert.InRange(second.Duration.TotalSeconds, 3.0, 3.6);

            // Every file has the same length, the old audio is intact, the new audio sits after it.
            Assert.Single(Directory.GetFiles(second.Folder!).Select(f => new FileInfo(f).Length).Distinct());
            var a = ReadSamples(Path.Combine(second.Folder!, "02_alpha.wav"));
            Assert.Equal(1000, a[0]);
            Assert.Equal(1000, a[(int)(1.5 * AudioFormat.SampleRate) * 2]);  // inside the first save, 0.5 s before its end
            Assert.Contains(a.Skip(2 * AudioFormat.SampleRate * 2), v => v == 1500);
            var b = ReadSamples(Path.Combine(second.Folder!, "05_beta.wav"));
            Assert.All(b.Take(2 * AudioFormat.SampleRate * 2), v => Assert.Equal(0, v)); // beta was silent during the first part
            Assert.Contains(b, v => v == 2500);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MergeOffOrExpiredWindowCreatesANewClip()
    {
        var dir = TempDir();
        try
        {
            var store = new TrackStore(TimeSpan.FromSeconds(30));
            var saver = new ReplaySaver(store);
            var track = new TrackInfo("alpha", 2);
            WriteTone(store, track, FrameClock.NowFrame - 3 * AudioFormat.SampleRate, 3 * AudioFormat.SampleRate, 1000);

            await saver.SaveAsync(2, dir, true, mergeWindowSeconds: 0);
            WriteTone(store, track, FrameClock.NowFrame - AudioFormat.SampleRate, AudioFormat.SampleRate, 1000);
            var off = await saver.SaveAsync(2, dir, true, mergeWindowSeconds: 0);
            Assert.False(off.Appended);
            Assert.Equal(2, Directory.GetDirectories(dir).Length);

            await Task.Delay(1300);
            WriteTone(store, track, FrameClock.NowFrame - AudioFormat.SampleRate, AudioFormat.SampleRate, 1000);
            var expired = await saver.SaveAsync(2, dir, true, mergeWindowSeconds: 1); // 1 s window, > 1.3 s passed
            Assert.False(expired.Appended);
            Assert.Equal(3, Directory.GetDirectories(dir).Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LockedPreviousClipFallsBackToASeparateClip()
    {
        var dir = TempDir();
        try
        {
            var store = new TrackStore(TimeSpan.FromSeconds(30));
            var saver = new ReplaySaver(store);
            var track = new TrackInfo("alpha", 2);
            WriteTone(store, track, FrameClock.NowFrame - 3 * AudioFormat.SampleRate, 3 * AudioFormat.SampleRate, 1000);
            var first = await saver.SaveAsync(2, dir, true, mergeWindowSeconds: 10);

            // An editor holding the file open without sharing makes extending impossible.
            using var held = new FileStream(Path.Combine(first.Folder!, "02_alpha.wav"), FileMode.Open, FileAccess.Read, FileShare.None);
            WriteTone(store, track, FrameClock.NowFrame - AudioFormat.SampleRate, AudioFormat.SampleRate, 1000);
            var second = await saver.SaveAsync(2, dir, true, mergeWindowSeconds: 10);

            Assert.True(second.Success, second.Message);
            Assert.False(second.Appended);
            Assert.NotEqual(first.Folder, second.Folder);
            Assert.NotNull(second.Message);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task NoNewSoundLeavesThePreviousClipUntouched()
    {
        var dir = TempDir();
        try
        {
            var store = new TrackStore(TimeSpan.FromSeconds(30));
            var saver = new ReplaySaver(store);
            WriteTone(store, new TrackInfo("alpha", 2), FrameClock.NowFrame - 3 * AudioFormat.SampleRate, 3 * AudioFormat.SampleRate, 1000);
            var first = await saver.SaveAsync(2, dir, true, mergeWindowSeconds: 10);
            var before = new FileInfo(Path.Combine(first.Folder!, "02_alpha.wav")).Length;

            var second = await saver.SaveAsync(2, dir, true, mergeWindowSeconds: 10);

            Assert.False(second.Success);
            Assert.Single(Directory.GetDirectories(dir));
            Assert.Equal(before, new FileInfo(Path.Combine(first.Folder!, "02_alpha.wav")).Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ReplayReportsEmptyBuffer()
    {
        var dir = TempDir();
        var result = await new ReplaySaver(new TrackStore(TimeSpan.FromSeconds(10))).SaveAsync(5, dir, true);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void ContinuousRecorderWritesLiveAudioToDisk()
    {
        var dir = TempDir();
        try
        {
            var store = new TrackStore(TimeSpan.FromSeconds(15));
            var track = new TrackInfo("live", 3);
            var recorder = new ContinuousRecorder(store, () => new RecorderOptions(dir, WriteMix: true, MaxPartMinutes: 60));

            recorder.Start();
            for (var i = 0; i < 15; i++)
            {
                WriteTone(store, track, FrameClock.NowFrame, AudioFormat.SampleRate / 10, 4000);
                Thread.Sleep(100);
            }

            var result = recorder.Stop();

            Assert.True(result.Success, result.Message);
            var wav = Path.Combine(result.Folder!, "03_live.wav");
            Assert.True(File.Exists(wav));
            Assert.True(File.Exists(Path.Combine(result.Folder!, "mix.wav")));

            var bytes = File.ReadAllBytes(wav);
            var seconds = (bytes.Length - 44) / 192_000.0;
            Assert.InRange(seconds, 1.2, 3.0);
            Assert.Contains(bytes.Skip(44).Chunk(2).Select(c => BitConverter.ToInt16(c)), v => v == 4000);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
