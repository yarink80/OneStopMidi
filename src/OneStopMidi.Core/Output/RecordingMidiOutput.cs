namespace OneStopMidi.Core.Output;

/// <summary>Output port that records every message in memory (tests, or silent operation).</summary>
public sealed class RecordingMidiOutputPort : IMidiOutputPort
{
    private readonly object _lock = new();
    private readonly List<byte[]> _messages = new();

    public RecordingMidiOutputPort(string name = "Recording") => Name = name;

    public string Name { get; }
    public bool IsDisposed { get; private set; }
    public int ResetCount { get; private set; }

    /// <summary>Snapshot of recorded messages (short messages are always 3 bytes).</summary>
    public IReadOnlyList<byte[]> Messages
    {
        get { lock (_lock) return _messages.ToArray(); }
    }

    public void Clear()
    {
        lock (_lock) _messages.Clear();
    }

    public void SendShort(byte status, byte data1, byte data2)
    {
        lock (_lock) _messages.Add(new[] { status, data1, data2 });
    }

    public void SendSysEx(ReadOnlySpan<byte> message)
    {
        var copy = message.ToArray();
        lock (_lock) _messages.Add(copy);
    }

    public void Reset()
    {
        lock (_lock) ResetCount++;
    }

    public void Dispose() => IsDisposed = true;

    /// <summary>Recorded note-ons (velocity &gt; 0) as (channel, note, velocity).</summary>
    public IReadOnlyList<(int Channel, int Note, int Velocity)> NoteOns =>
        Messages.Where(m => m.Length == 3 && (m[0] & 0xF0) == 0x90 && m[2] > 0)
            .Select(m => (m[0] & 0x0F, (int)m[1], (int)m[2])).ToArray();

    /// <summary>Recorded note-offs (0x80 or 0x90 with velocity 0) as (channel, note).</summary>
    public IReadOnlyList<(int Channel, int Note)> NoteOffs =>
        Messages.Where(m => m.Length == 3 && ((m[0] & 0xF0) == 0x80 || ((m[0] & 0xF0) == 0x90 && m[2] == 0)))
            .Select(m => (m[0] & 0x0F, (int)m[1])).ToArray();

    /// <summary>Recorded control changes as (channel, controller, value).</summary>
    public IReadOnlyList<(int Channel, int Controller, int Value)> ControlChanges =>
        Messages.Where(m => m.Length == 3 && (m[0] & 0xF0) == 0xB0)
            .Select(m => (m[0] & 0x0F, (int)m[1], (int)m[2])).ToArray();

    /// <summary>Recorded program changes as (channel, program).</summary>
    public IReadOnlyList<(int Channel, int Program)> ProgramChanges =>
        Messages.Where(m => m.Length == 3 && (m[0] & 0xF0) == 0xC0)
            .Select(m => (m[0] & 0x0F, (int)m[1])).ToArray();
}

/// <summary>Provider with a fixed number of in-memory recording devices.</summary>
public sealed class RecordingMidiOutputProvider : IMidiOutputProvider
{
    private readonly int _deviceCount;

    public RecordingMidiOutputProvider(int deviceCount = 2) => _deviceCount = deviceCount;

    /// <summary>Every port opened so far, keyed by device id (latest wins).</summary>
    public Dictionary<int, RecordingMidiOutputPort> OpenedPorts { get; } = new();

    public IReadOnlyList<MidiDeviceInfo> GetDevices() =>
        Enumerable.Range(0, _deviceCount).Select(i => new MidiDeviceInfo(i, $"Recording device {i}")).ToArray();

    public IMidiOutputPort? Open(int deviceId)
    {
        if (deviceId < 0 || deviceId >= _deviceCount) return null;
        var port = new RecordingMidiOutputPort($"Recording device {deviceId}");
        lock (OpenedPorts) OpenedPorts[deviceId] = port;
        return port;
    }
}
