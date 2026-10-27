namespace OneStopMidi.Core.Output;

/// <summary>An open MIDI output. Implementations must not throw from Send* on device errors.</summary>
public interface IMidiOutputPort : IDisposable
{
    string Name { get; }

    /// <summary>Sends a 1-3 byte channel/system message.</summary>
    void SendShort(byte status, byte data1, byte data2);

    /// <summary>Sends a complete system exclusive message (normally F0 ... F7).</summary>
    void SendSysEx(ReadOnlySpan<byte> message);

    /// <summary>Device reset (turns off all notes).</summary>
    void Reset();
}

/// <summary>Enumerates and opens output devices.</summary>
public interface IMidiOutputProvider
{
    IReadOnlyList<MidiDeviceInfo> GetDevices();

    /// <summary>Opens a device; returns null (never throws) when it cannot be opened.</summary>
    IMidiOutputPort? Open(int deviceId);
}
