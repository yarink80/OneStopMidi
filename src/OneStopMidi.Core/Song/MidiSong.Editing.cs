using OneStopMidi.Core.Midi;

namespace OneStopMidi.Core;

/// <summary>A copied tick range: per-track events with ticks relative to the range start.</summary>
public sealed class SongClip
{
    internal SongClip(int ppq, long length, List<List<MidiEvent>> tracks)
    {
        Ppq = ppq;
        Length = length;
        Tracks = tracks;
    }

    public int Ppq { get; }
    public long Length { get; }
    internal List<List<MidiEvent>> Tracks { get; }
    public int EventCount => Tracks.Sum(t => t.Count);
}

/// <summary>Full copy of the editable song state, used for undo/redo.</summary>
internal sealed record SongSnapshot(List<MidiTrack> Tracks, int Format, long Version);

public sealed partial class MidiSong
{
    // Meta events that describe the track rather than musical content; kept when their range is deleted.
    private static bool IsStructuralMeta(MidiEvent e) =>
        e.IsMeta && e.MetaType is MidiEvent.MetaSequenceNumber or MidiEvent.MetaCopyright or MidiEvent.MetaTrackName
            or MidiEvent.MetaInstrumentName or MidiEvent.MetaChannelPrefix or MidiEvent.MetaPortPrefix;

    internal SongSnapshot CaptureSnapshot() => new(_tracks.Select(t => t.Clone()).ToList(), Format, Version);

    internal void RestoreSnapshot(SongSnapshot s)
    {
        _tracks = s.Tracks.Select(t => t.Clone()).ToList();
        Format = s.Format;
        Version = s.Version;
        Analyze();
    }

    /// <summary>Copies [from, to). Notes still sounding at <paramref name="to"/> get a note-off at the range end.</summary>
    internal SongClip CopyRange(long from, long to)
    {
        long len = Math.Max(0, to - from);
        var clipTracks = new List<List<MidiEvent>>();
        foreach (var track in _tracks)
        {
            var list = new List<MidiEvent>();
            var open = new Dictionary<int, int>();
            foreach (var e in track.Events)
            {
                if (e.Tick < from || e.Tick >= to) continue;
                if (IsStructuralMeta(e) || IsLocatorMeta(e)) continue;
                int key = e.Channel * 128 + e.Data1;
                if (e.IsNoteOn)
                {
                    open[key] = open.GetValueOrDefault(key) + 1;
                }
                else if (e.IsNoteOff)
                {
                    if (open.GetValueOrDefault(key) == 0) continue; // its note-on is before the range
                    open[key]--;
                }
                list.Add(e with { Tick = e.Tick - from });
            }
            foreach (var (key, count) in open)
                for (int i = 0; i < count; i++)
                    list.Add(MidiEvent.NoteOff(len, key / 128, key % 128));
            clipTracks.Add(list);
        }
        return new SongClip(Ppq, len, clipTracks);
    }

    /// <summary>Removes [from, to) and shifts later events left. Notes cut in half are closed properly.</summary>
    internal void DeleteRange(long from, long to)
    {
        long len = to - from;
        if (len <= 0) return;
        foreach (var track in _tracks)
        {
            var result = new List<MidiEvent>(track.Events.Count);
            var openBefore = new Dictionary<int, int>();
            var openInside = new Dictionary<int, int>();
            foreach (var e in track.Events)
            {
                int key = e.Channel * 128 + e.Data1;
                if (e.Tick < from)
                {
                    if (e.IsNoteOn) openBefore[key] = openBefore.GetValueOrDefault(key) + 1;
                    else if (e.IsNoteOff && openBefore.GetValueOrDefault(key) > 0) openBefore[key]--;
                    result.Add(e);
                }
                else if (e.Tick < to)
                {
                    if (e.IsNoteOn) openInside[key] = openInside.GetValueOrDefault(key) + 1;
                    else if (e.IsNoteOff)
                    {
                        if (openInside.GetValueOrDefault(key) > 0) openInside[key]--;
                        else if (openBefore.GetValueOrDefault(key) > 0) { openBefore[key]--; result.Add(e with { Tick = from }); }
                    }
                    else if (IsStructuralMeta(e)) result.Add(e with { Tick = from });
                }
                else
                {
                    if (e.IsNoteOff && openInside.GetValueOrDefault(key) > 0) { openInside[key]--; continue; }
                    result.Add(e with { Tick = e.Tick - len });
                }
            }
            track.Events.Clear();
            track.Events.AddRange(result);
            if (track.EndTick >= to) track.EndTick -= len;
            else if (track.EndTick > from) track.EndTick = from;
        }
        MarkModified();
    }

    /// <summary>Inserts a clip at <paramref name="at"/>, shifting events at or after it to the right.</summary>
    internal void InsertClip(SongClip clip, long at)
    {
        double scale = (double)Ppq / clip.Ppq;
        long len = (long)Math.Round(clip.Length * scale);
        if (len <= 0 && clip.EventCount == 0) return;

        var sources = clip.Tracks;
        if (Format == 0 && sources.Count > 1)
            sources = new List<List<MidiEvent>> { sources.SelectMany(t => t).OrderBy(e => e.Tick).ToList() };
        while (_tracks.Count < sources.Count) _tracks.Add(new MidiTrack());

        for (int ti = 0; ti < _tracks.Count; ti++)
        {
            var track = _tracks[ti];
            var insert = ti < sources.Count ? sources[ti] : new List<MidiEvent>();
            var before = new List<MidiEvent>();
            var after = new List<MidiEvent>();
            foreach (var e in track.Events)
            {
                // A note-off exactly at the insertion point belongs to the material before it.
                if (e.Tick < at || (e.Tick == at && e.IsNoteOff)) before.Add(e);
                else after.Add(e with { Tick = e.Tick + len });
            }
            track.Events.Clear();
            track.Events.AddRange(before);
            track.Events.AddRange(insert.Select(e => e with { Tick = at + (long)Math.Round(e.Tick * scale) }));
            track.Events.AddRange(after);
            if (track.EndTick >= at) track.EndTick += len;
            if (insert.Count > 0) track.EndTick = Math.Max(track.EndTick, at + len);
            track.EndTick = Math.Max(track.EndTick, track.LastEventTick);
        }
        MarkModified();
    }

    /// <summary>Removes all channel messages of the flagged channels. Returns the number of removed events.</summary>
    internal int EraseChannels(IReadOnlyList<bool> channels)
    {
        int removed = 0;
        foreach (var track in _tracks)
            removed += track.Events.RemoveAll(e => e.IsChannelMessage && channels[e.Channel]);
        if (removed > 0) MarkModified();
        return removed;
    }

    internal void SetSequenceName(string name)
    {
        var bytes = TextEncoding.GetBytes(name);
        var track = _tracks[0];
        int idx = track.Events.FindIndex(e => e.IsMeta && e.MetaType == MidiEvent.MetaTrackName);
        if (idx >= 0) track.Events[idx] = track.Events[idx] with { Data = bytes };
        else track.Events.Insert(0, MidiEvent.Meta(0, MidiEvent.MetaTrackName, bytes));
        MarkModified();
    }
}
