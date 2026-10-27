using System.Text;
using OneStopMidi.Core.Midi;

namespace OneStopMidi.Core;

/// <summary>
/// Extracts lyrics and embedded chord symbols. Lyric meta events (FF 05) are preferred; otherwise text events
/// (FF 01) of the most text-rich track are used, karaoke style: '@' = header info (skipped), '/' = new line,
/// '\' = new paragraph. CR/LF inside lyric text also break lines.
/// </summary>
public static class SongTextAnalyzer
{
    /// <summary>Minimum number of text events for a non-karaoke file's text track to be shown as lyrics.</summary>
    public const int MinTextEventsForLyrics = 8;

    public static (IReadOnlyList<LyricSyllable> Lyrics, IReadOnlyList<ChordSymbol> Chords) Analyze(
        IReadOnlyList<MidiTrack> tracks, Encoding encoding)
    {
        var chords = new List<ChordSymbol>();
        var consumed = new HashSet<MidiEvent>(ReferenceEqualityComparer.Instance);
        bool karaoke = false;

        foreach (var track in tracks)
        {
            foreach (var e in track.Events)
            {
                if (!e.IsMeta) continue;
                if (e.MetaType == MidiEvent.MetaSequencerSpecific)
                {
                    var name = ChordParser.DecodeManufacturerChord(e.Data);
                    if (name is not null) chords.Add(new ChordSymbol(e.Tick, name, false));
                    continue;
                }
                if (e.MetaType is not (MidiEvent.MetaText or MidiEvent.MetaLyric or MidiEvent.MetaMarker or MidiEvent.MetaCuePoint))
                    continue;
                string text = Decode(e.Data, encoding);
                if (e.MetaType == MidiEvent.MetaText && text.StartsWith('@')) karaoke = true;
                bool allowPlain = e.MetaType is MidiEvent.MetaText or MidiEvent.MetaLyric;
                if (ChordParser.TryParseEvent(text, allowPlain, out var chord))
                {
                    chords.Add(new ChordSymbol(e.Tick, chord, false));
                    consumed.Add(e);
                }
            }
        }

        // Source selection.
        var source = new List<MidiEvent>();
        foreach (var track in tracks)
            source.AddRange(track.Events.Where(e => e.IsMeta && e.MetaType == MidiEvent.MetaLyric && !consumed.Contains(e)));
        if (!source.Any(e => Decode(e.Data, encoding).Trim().Length > 0))
        {
            source.Clear();
            List<MidiEvent>? best = null;
            foreach (var track in tracks)
            {
                var texts = track.Events.Where(e => e.IsMeta && e.MetaType == MidiEvent.MetaText && !consumed.Contains(e)
                                                    && !Decode(e.Data, encoding).StartsWith('@')).ToList();
                if (best is null || texts.Count > best.Count) best = texts;
            }
            if (best is { Count: > 0 } && (karaoke || (best.Count >= MinTextEventsForLyrics && best.Select(e => e.Tick).Distinct().Count() > 1)))
                source = best;
        }
        source = source.OrderBy(e => e.Tick).ToList(); // stable

        var lyrics = new List<LyricSyllable>();
        bool pendingLine = false, pendingPara = false;
        foreach (var e in source)
        {
            string text = Decode(e.Data, encoding);
            var inline = new List<string>();
            text = ChordParser.StripInlineChords(text, inline);
            foreach (var c in inline) chords.Add(new ChordSymbol(e.Tick, c, false));

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) pendingLine = true;
                string part = parts[i];
                while (part.Length > 0 && (part[0] == '/' || part[0] == '\\'))
                {
                    if (part[0] == '\\') pendingPara = true; else pendingLine = true;
                    part = part[1..];
                }
                if (part.Length == 0) continue;
                lyrics.Add(new LyricSyllable(e.Tick, part, pendingLine || pendingPara, pendingPara));
                pendingLine = pendingPara = false;
            }
        }

        chords = chords.OrderBy(c => c.Tick).ToList();
        return (lyrics, chords);
    }

    public static string Decode(byte[]? data, Encoding encoding)
    {
        if (data is null || data.Length == 0) return "";
        int len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--; // some files NUL-terminate text
        return encoding.GetString(data, 0, len);
    }
}
