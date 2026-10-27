using System.Text;
using OneStopMidi.Core;
using OneStopMidi.Core.Midi;
using OneStopMidi.Core.Output;

namespace OneStopMidi.Core.Tests;

/// <summary>Small fluent builder for test MIDI files.</summary>
internal sealed class SongBuilder
{
    private readonly List<TrackBuilder> _tracks = new();

    public SongBuilder(int ppq = 480, int format = 1)
    {
        Ppq = ppq;
        Format = format;
    }

    public int Ppq { get; }
    public int Format { get; }

    public TrackBuilder Track()
    {
        var t = new TrackBuilder(this);
        _tracks.Add(t);
        return t;
    }

    public SmfFile ToSmf()
    {
        var f = new SmfFile { Format = Format, Ppq = Ppq };
        foreach (var t in _tracks) f.Tracks.Add(new MidiTrack(t.Events.OrderBy(e => e.Tick), t.EndTick));
        return f;
    }

    public byte[] Build() => SmfWriter.Write(ToSmf());

    public MidiSong Song(SongLoadOptions? options = null) => MidiSong.FromBytes(Build(), options);
}

internal sealed class TrackBuilder
{
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private readonly SongBuilder _owner;

    public TrackBuilder(SongBuilder owner) => _owner = owner;

    public List<MidiEvent> Events { get; } = new();
    public long EndTick { get; set; }

    public SongBuilder Done() => _owner;

    public TrackBuilder Add(MidiEvent e) { Events.Add(e); return this; }
    public TrackBuilder Name(string s) => Add(MidiEvent.Meta(0, MidiEvent.MetaTrackName, Latin1.GetBytes(s)));
    public TrackBuilder Text(long tick, string s) => Add(MidiEvent.Meta(tick, MidiEvent.MetaText, Latin1.GetBytes(s)));
    public TrackBuilder Lyric(long tick, string s) => Add(MidiEvent.Meta(tick, MidiEvent.MetaLyric, Latin1.GetBytes(s)));
    public TrackBuilder Tempo(long tick, double bpm) => Add(MidiEvent.TempoBpm(tick, bpm));
    public TrackBuilder TimeSig(long tick, int num, int den) => Add(MidiEvent.TimeSignature(tick, num, den));
    public TrackBuilder Program(long tick, int ch, int program) => Add(MidiEvent.ProgramChange(tick, ch, program));
    public TrackBuilder CC(long tick, int ch, int controller, int value) => Add(MidiEvent.ControlChange(tick, ch, controller, value));

    public TrackBuilder Note(int ch, int note, long start, long length, int velocity = 100)
    {
        Add(MidiEvent.NoteOn(start, ch, note, velocity));
        return Add(MidiEvent.NoteOff(start + length, ch, note));
    }

    public TrackBuilder End(long tick) { EndTick = tick; return this; }
}

/// <summary>Deterministic engine: manual clock, recording ports, no threads, synchronous events.</summary>
internal sealed class EngineHarness : IDisposable
{
    public EngineHarness(MidiSong? song = null)
    {
        Provider = new RecordingMidiOutputProvider(2);
        Clock = new ManualClock();
        Engine = new PlayerEngine(Provider, Clock, realtime: false);
        Engine.SongEnded += (_, _) => SongEndedCount++;
        if (song is not null) Engine.Load(song);
        Port.Clear();
    }

    public RecordingMidiOutputProvider Provider { get; }
    public ManualClock Clock { get; }
    public PlayerEngine Engine { get; }
    public int SongEndedCount { get; private set; }
    public RecordingMidiOutputPort Port => Provider.OpenedPorts[0];

    /// <summary>Advances the clock in small steps (like the playback thread would) and pumps.</summary>
    public void Advance(double seconds, double step = 0.005)
    {
        double start = Clock.Seconds;
        int n = Math.Max(1, (int)Math.Ceiling(seconds / step));
        for (int i = 1; i <= n; i++)
        {
            Clock.Seconds = start + seconds * i / n;
            Engine.Pump();
        }
    }

    public void Dispose() => Engine.Dispose();
}
