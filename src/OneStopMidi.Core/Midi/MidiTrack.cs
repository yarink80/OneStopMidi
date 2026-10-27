namespace OneStopMidi.Core.Midi;

/// <summary>A track: events in file order with absolute ticks (end-of-track is not stored as an event).</summary>
public sealed class MidiTrack
{
    public MidiTrack() { }

    public MidiTrack(IEnumerable<MidiEvent> events, long endTick = 0)
    {
        Events.AddRange(events);
        EndTick = Math.Max(endTick, LastEventTick);
    }

    /// <summary>Events sorted by tick (stable, file order within a tick).</summary>
    public List<MidiEvent> Events { get; } = new();

    /// <summary>Tick of the end-of-track marker (at least the last event tick).</summary>
    public long EndTick { get; set; }

    public long LastEventTick => Events.Count == 0 ? 0 : Events[^1].Tick;

    public MidiTrack Clone() => new(Events, EndTick);

    /// <summary>First track-name meta event (raw bytes) or null.</summary>
    public byte[]? NameBytes => Events.FirstOrDefault(e => e.IsMeta && e.MetaType == MidiEvent.MetaTrackName)?.Data;
}

/// <summary>Raw contents of a Standard MIDI File.</summary>
public sealed class SmfFile
{
    public int Format { get; set; } = 1;

    /// <summary>Pulses per quarter note (SMPTE divisions are converted on read).</summary>
    public int Ppq { get; set; } = 480;

    public List<MidiTrack> Tracks { get; } = new();

    /// <summary>Non-fatal problems found while reading (truncation, bad bytes).</summary>
    public List<string> ReadIssues { get; } = new();
}
