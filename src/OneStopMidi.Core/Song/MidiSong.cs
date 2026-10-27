using System.Text;
using OneStopMidi.Core.Midi;

namespace OneStopMidi.Core;

/// <summary>
/// A loaded Standard MIDI File: per-track events plus derived views (merged events, tempo and time-signature maps,
/// lyrics, chords, per-channel track names and warnings). Editing members are internal and used by the engine.
/// </summary>
public sealed partial class MidiSong : IMidiSong
{
    /// <summary>Private ID used in the sequencer-specific meta event (FF 7F) that stores the locators.</summary>
    internal static readonly byte[] LocatorMetaId = { 0x00, 0x4D, 0x6C, 0x01 };

    private static long s_versionCounter;

    private List<MidiTrack> _tracks;
    private IReadOnlyList<MidiEvent> _events = Array.Empty<MidiEvent>();
    private IReadOnlyList<ChordSymbol>? _detected;

    static MidiSong()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Windows-1252, the default for MIDI text.</summary>
    public static Encoding DefaultTextEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
    }

    public MidiSong(SmfFile file, SongLoadOptions? options = null, string? filePath = null)
    {
        FilePath = filePath;
        Format = file.Format;
        Ppq = file.Ppq;
        TextEncoding = options?.TextEncoding ?? DefaultTextEncoding;
        ChordMode = options?.ChordMode ?? ChordDisplayMode.PreferEmbeddedThenDetect;
        ReadIssues = file.ReadIssues.ToArray();
        _tracks = file.Tracks.Select(t => t.Clone()).ToList();
        if (_tracks.Count == 0) _tracks.Add(new MidiTrack());
        ExtractLocators();
        Analyze();
        Version = SavedVersion = NextVersion();
    }

    public static MidiSong Load(string path, SongLoadOptions? options = null) =>
        new(SmfReader.ReadFile(path), options, Path.GetFullPath(path));

    public static MidiSong FromBytes(byte[] data, SongLoadOptions? options = null, string? filePath = null) =>
        new(SmfReader.Read(data), options, filePath);

    // ---- IMidiSong ----
    public string? FilePath { get; internal set; }
    public string? SequenceName { get; private set; }
    public int Format { get; private set; }
    public int Ppq { get; }
    public int TrackCount => _tracks.Count;
    public long LengthTicks { get; private set; }
    public TimeSpan Duration => TempoMap.TickToTime(LengthTicks);
    public bool IsModified => Version != SavedVersion;
    public IReadOnlyList<TimeSignatureChange> TimeSignatures { get; private set; } = Array.Empty<TimeSignatureChange>();
    public IReadOnlyList<TempoChange> Tempos { get; private set; } = Array.Empty<TempoChange>();
    public IReadOnlyList<LyricSyllable> Lyrics { get; private set; } = Array.Empty<LyricSyllable>();
    public IReadOnlyList<ChordSymbol> Chords { get; private set; } = Array.Empty<ChordSymbol>();
    public IReadOnlyList<string> Warnings { get; private set; } = Array.Empty<string>();

    public MusicalPosition ToMusical(long tick) => TimeSignatureMap.ToMusical(tick);
    public long FromMusical(MusicalPosition position) => TimeSignatureMap.FromMusical(position);
    public TimeSpan TickToTime(long tick) => TempoMap.TickToTime(tick);

    // ---- extra views ----
    public Encoding TextEncoding { get; }
    public ChordDisplayMode ChordMode { get; }
    public IReadOnlyList<string> ReadIssues { get; }
    public IReadOnlyList<MidiTrack> Tracks => _tracks;

    /// <summary>All events of all tracks merged by tick (ties: track order, then file order).</summary>
    public IReadOnlyList<MidiEvent> Events => _events;

    public TempoMap TempoMap { get; private set; } = TempoMap.Constant(480, 120);
    public TimeSignatureMap TimeSignatureMap { get; private set; } = new(480, Array.Empty<TimeSignatureChange>());

    /// <summary>Number of tempo meta events actually present in the file.</summary>
    public int TempoEventCount { get; private set; }

    /// <summary>Chord symbols found in the file (text/lyric/marker events and manufacturer chord metas).</summary>
    public IReadOnlyList<ChordSymbol> EmbeddedChords { get; private set; } = Array.Empty<ChordSymbol>();

    /// <summary>Auto-detected chords (computed on first use).</summary>
    public IReadOnlyList<ChordSymbol> DetectedChords =>
        _detected ??= ChordDetector.Detect(_events, TimeSignatureMap, LengthTicks);

    /// <summary>Track name per channel (0-15), or null.</summary>
    public IReadOnlyList<string?> ChannelTrackNames { get; private set; } = new string?[16];

    /// <summary>Whether each channel (0-15) has note-on events.</summary>
    public IReadOnlyList<bool> ChannelHasNotes { get; private set; } = new bool[16];

    /// <summary>Locators stored in the file by a previous save, if any.</summary>
    public long? StoredLeftLocator { get; private set; }
    public long? StoredRightLocator { get; private set; }

    internal long Version { get; set; }
    internal long SavedVersion { get; set; }

    internal static long NextVersion() => Interlocked.Increment(ref s_versionCounter);

    internal void MarkModified()
    {
        Version = NextVersion();
        Analyze();
    }

    // ---- analysis ----
    internal void Analyze()
    {
        foreach (var t in _tracks) t.EndTick = Math.Max(t.EndTick, t.LastEventTick);

        var merged = new List<(MidiEvent E, int Track, int Index)>();
        for (int ti = 0; ti < _tracks.Count; ti++)
        {
            var evs = _tracks[ti].Events;
            for (int i = 0; i < evs.Count; i++) merged.Add((evs[i], ti, i));
        }
        merged.Sort((a, b) =>
        {
            int c = a.E.Tick.CompareTo(b.E.Tick);
            if (c != 0) return c;
            c = a.Track.CompareTo(b.Track);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        _events = merged.Select(m => m.E).ToArray();
        _detected = null;

        LengthTicks = _tracks.Count == 0 ? 0 : _tracks.Max(t => t.EndTick);

        var tempoEvents = _events.Where(e => e.TempoMicroseconds.HasValue).ToList();
        TempoEventCount = tempoEvents.Count;
        TempoMap = new TempoMap(Ppq, tempoEvents.Select(e => (e.Tick, e.TempoMicroseconds!.Value)));
        Tempos = TempoMap.Changes;

        var sigs = _events.Where(e => e.IsMeta && e.MetaType == MidiEvent.MetaTimeSignature && e.Data is { Length: >= 2 })
            .Select(e => new TimeSignatureChange(e.Tick, Math.Max(1, (int)e.Data![0]), 1 << Math.Min(6, (int)e.Data[1])));
        TimeSignatureMap = new TimeSignatureMap(Ppq, sigs);
        TimeSignatures = TimeSignatureMap.Changes;

        var nameBytes = _tracks[0].NameBytes;
        SequenceName = nameBytes is null ? null : SongTextAnalyzer.Decode(nameBytes, TextEncoding).Trim();

        AnalyzeChannels();

        var (lyrics, chords) = SongTextAnalyzer.Analyze(_tracks, TextEncoding);
        Lyrics = lyrics;
        EmbeddedChords = chords;
        Chords = ChordMode switch
        {
            ChordDisplayMode.Off => Array.Empty<ChordSymbol>(),
            ChordDisplayMode.Embedded => EmbeddedChords,
            ChordDisplayMode.AutoDetect => DetectedChords,
            _ => EmbeddedChords.Count > 0 ? EmbeddedChords : DetectedChords,
        };
    }

    private void AnalyzeChannels()
    {
        var hasNotes = new bool[16];
        var names = new string?[16];
        for (int ti = 0; ti < _tracks.Count; ti++)
        {
            var track = _tracks[ti];
            var channels = new HashSet<int>();
            string? trackName = null;
            int prefix = -1;
            foreach (var e in track.Events)
            {
                if (e.IsNoteOn) { channels.Add(e.Channel); hasNotes[e.Channel] = true; }
                else if (e.IsMeta && e.MetaType == MidiEvent.MetaChannelPrefix && e.Data is { Length: >= 1 })
                    prefix = e.Data[0] & 0x0F;
                else if (e.IsMeta && e.MetaType == MidiEvent.MetaTrackName)
                {
                    string n = SongTextAnalyzer.Decode(e.Data, TextEncoding).Trim();
                    if (n.Length == 0) continue;
                    if (prefix >= 0) names[prefix] ??= n;
                    else trackName ??= n;
                }
            }
            if (ti == 0 || trackName is null) continue; // track 0's name is the sequence name
            foreach (int ch in channels.Order()) names[ch] ??= trackName;
        }
        ChannelHasNotes = hasNotes;
        ChannelTrackNames = names;

        var warnings = new List<string>();
        for (int ch = 0; ch < 16; ch++)
        {
            if (!hasNotes[ch]) continue;
            bool program = false, volume = false;
            foreach (var e in _events)
            {
                if (!e.IsChannelMessage || e.Channel != ch) continue;
                if (e.IsNoteOn) break;
                if (e.Command == 0xC0) program = true;
                else if (e.Command == 0xB0 && e.Data1 == 7) volume = true;
            }
            if (!program && ch != 9) warnings.Add($"Channel {ch + 1}: no program change before the first note.");
            if (!volume) warnings.Add($"Channel {ch + 1}: no volume (CC7) before the first note.");
        }
        Warnings = warnings;
    }

    private void ExtractLocators()
    {
        foreach (var track in _tracks)
        {
            for (int i = track.Events.Count - 1; i >= 0; i--)
            {
                var e = track.Events[i];
                if (!IsLocatorMeta(e)) continue;
                var d = e.Data!;
                StoredLeftLocator = ReadBE32(d, 4);
                StoredRightLocator = ReadBE32(d, 8);
                track.Events.RemoveAt(i);
            }
        }
    }

    internal static bool IsLocatorMeta(MidiEvent e) =>
        e.IsMeta && e.MetaType == MidiEvent.MetaSequencerSpecific && e.Data is { Length: >= 12 } d
        && d.AsSpan(0, 4).SequenceEqual(LocatorMetaId);

    internal static MidiEvent CreateLocatorMeta(long left, long right)
    {
        var d = new byte[12];
        LocatorMetaId.CopyTo(d, 0);
        WriteBE32(d, 4, (uint)Math.Clamp(left, 0, uint.MaxValue));
        WriteBE32(d, 8, (uint)Math.Clamp(right, 0, uint.MaxValue));
        return MidiEvent.Meta(0, MidiEvent.MetaSequencerSpecific, d);
    }

    private static long ReadBE32(byte[] d, int p) => (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

    private static void WriteBE32(byte[] d, int p, uint v)
    {
        d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v;
    }

    // ---- saving ----

    /// <summary>Builds the file representation in the requested format (null = current format, else 0 or 1).</summary>
    public SmfFile ToSmf(int? format = null, long? leftLocator = null, long? rightLocator = null)
    {
        int target = format is 0 or 1 ? format.Value : Format;
        var tracks = ConvertTracks(target);
        if (leftLocator.HasValue || rightLocator.HasValue)
            tracks[0].Events.Insert(0, CreateLocatorMeta(leftLocator ?? 0, rightLocator ?? 0));
        var file = new SmfFile { Format = target, Ppq = Ppq };
        file.Tracks.AddRange(tracks);
        return file;
    }

    /// <summary>Writes the song; converts the in-memory structure when the format changes.</summary>
    public void Save(string path, int? format = null, long? leftLocator = null, long? rightLocator = null)
    {
        int target = format is 0 or 1 ? format.Value : Format;
        SmfWriter.WriteFile(path, ToSmf(target, leftLocator, rightLocator));
        if (target != Format)
        {
            _tracks = ConvertTracks(target);
            Format = target;
            Analyze();
        }
        StoredLeftLocator = leftLocator;
        StoredRightLocator = rightLocator;
        FilePath = Path.GetFullPath(path);
        SavedVersion = Version;
    }

    private List<MidiTrack> ConvertTracks(int target)
    {
        if (target == 0 && _tracks.Count > 1) return new List<MidiTrack> { MergeToSingleTrack() };
        if (target == 1 && Format == 0 && _tracks.Count == 1) return SplitByChannel(_tracks[0]);
        return _tracks.Select(t => t.Clone()).ToList();
    }

    private MidiTrack MergeToSingleTrack()
    {
        var all = new List<(MidiEvent E, int Track, int Index)>();
        for (int ti = 0; ti < _tracks.Count; ti++)
        {
            var track = _tracks[ti];
            var channels = track.Events.Where(e => e.IsChannelMessage).Select(e => e.Channel).Distinct().ToList();
            for (int i = 0; i < track.Events.Count; i++)
            {
                var e = track.Events[i];
                if (ti > 0 && e.IsMeta && e.MetaType == MidiEvent.MetaTrackName)
                {
                    // Keep the per-channel name via a channel prefix when the track uses a single channel.
                    if (channels.Count != 1) continue;
                    all.Add((MidiEvent.Meta(e.Tick, MidiEvent.MetaChannelPrefix, new[] { (byte)channels[0] }), ti, i));
                }
                all.Add((e, ti, i));
            }
        }
        all.Sort((a, b) =>
        {
            int c = a.E.Tick.CompareTo(b.E.Tick);
            if (c != 0) return c;
            c = a.Track.CompareTo(b.Track);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return new MidiTrack(all.Select(a => a.E), _tracks.Max(t => t.EndTick));
    }

    private static List<MidiTrack> SplitByChannel(MidiTrack source)
    {
        var conductor = new MidiTrack { EndTick = source.EndTick };
        var perChannel = new MidiTrack?[16];
        var names = new MidiEvent?[16];
        int prefix = -1;
        foreach (var e in source.Events)
        {
            if (e.IsChannelMessage)
            {
                (perChannel[e.Channel] ??= new MidiTrack { EndTick = source.EndTick }).Events.Add(e);
            }
            else if (e.IsMeta && e.MetaType == MidiEvent.MetaChannelPrefix && e.Data is { Length: >= 1 })
            {
                prefix = e.Data[0] & 0x0F;
            }
            else if (e.IsMeta && e.MetaType == MidiEvent.MetaTrackName && prefix >= 0)
            {
                names[prefix] ??= e with { Tick = 0 };
            }
            else
            {
                conductor.Events.Add(e);
            }
        }
        var result = new List<MidiTrack> { conductor };
        for (int ch = 0; ch < 16; ch++)
        {
            var t = perChannel[ch];
            if (t is null) continue;
            if (names[ch] is { } n) t.Events.Insert(0, n);
            result.Add(t);
        }
        return result;
    }
}
