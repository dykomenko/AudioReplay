using System.Text.Json;
using AudioReplay.Core.Diagnostics;

namespace AudioReplay.Core.Audio;

/// <summary>
/// Persistent app -> slot mapping. A slot is assigned the first time an app is seen and never changes or
/// gets reused, which is what keeps an application on the "same track" across all recordings.
/// The file (tracks.json) can be edited by hand to reorder slots.
/// </summary>
public sealed class TrackRegistry
{
    public const string MicrophoneId = "microphone";

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, int> _slots = new(StringComparer.Ordinal);
    private int _nextSlot = 1;

    public TrackRegistry(string? path)
    {
        _path = path;
        Load();
        if (!_slots.ContainsKey(MicrophoneId))
        {
            // The microphone always sits in the first slot.
            _slots[MicrophoneId] = _nextSlot++;
            Save();
        }
    }

    public TrackInfo Get(string id)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(id, out var slot))
            {
                slot = _nextSlot++;
                _slots[id] = slot;
                Save();
            }

            return new TrackInfo(id, slot);
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path));
            if (data?.Slots is null) return;
            foreach (var (id, slot) in data.Slots) _slots[id] = slot;
            _nextSlot = Math.Max(data.NextSlot, (_slots.Count == 0 ? 0 : _slots.Values.Max()) + 1);
        }
        catch (Exception ex)
        {
            Log.Warn($"tracks.json is unreadable, starting a new mapping: {ex.Message}");
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new Data { NextSlot = _nextSlot, Slots = new(_slots) },
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot persist tracks.json: {ex.Message}");
        }
    }

    private sealed class Data
    {
        public int NextSlot { get; set; } = 1;

        public Dictionary<string, int> Slots { get; set; } = new();
    }
}
