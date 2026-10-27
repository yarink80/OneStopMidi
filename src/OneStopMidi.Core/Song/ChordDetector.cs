using OneStopMidi.Core.Midi;

namespace OneStopMidi.Core;

/// <summary>
/// Simple template-matching chord detector: per beat, weights each pitch class by how long it sounds,
/// scores all chord templates for all roots and emits a symbol whenever the best chord changes.
/// </summary>
public static class ChordDetector
{
    private static readonly (string Suffix, int[] Intervals, double Bias)[] Templates =
    {
        ("", new[] { 0, 4, 7 }, 0.06),
        ("m", new[] { 0, 3, 7 }, 0.05),
        ("7", new[] { 0, 4, 7, 10 }, 0.02),
        ("maj7", new[] { 0, 4, 7, 11 }, 0.0),
        ("m7", new[] { 0, 3, 7, 10 }, 0.01),
        ("6", new[] { 0, 4, 7, 9 }, 0.0),
        ("m6", new[] { 0, 3, 7, 9 }, 0.0),
        ("dim", new[] { 0, 3, 6 }, 0.0),
        ("dim7", new[] { 0, 3, 6, 9 }, 0.0),
        ("m7b5", new[] { 0, 3, 6, 10 }, 0.0),
        ("aug", new[] { 0, 4, 8 }, 0.0),
        ("sus4", new[] { 0, 5, 7 }, 0.0),
        ("sus2", new[] { 0, 2, 7 }, -0.01),
    };

    /// <summary>Detects chords over the song. Notes on <paramref name="drumChannel"/> are ignored.</summary>
    public static IReadOnlyList<ChordSymbol> Detect(IReadOnlyList<MidiEvent> events, TimeSignatureMap signatures, long lengthTicks, int drumChannel = 9)
    {
        var notes = CollectNotes(events, drumChannel);
        var result = new List<ChordSymbol>();
        if (notes.Count == 0) return result;
        notes.Sort((a, b) => a.Start.CompareTo(b.Start));

        string? last = null;
        int first = 0;
        long beatStart = 0;
        var weights = new double[12];
        while (beatStart < lengthTicks)
        {
            long beatLen = signatures.BeatTicksAt(beatStart);
            long beatEnd = beatStart + beatLen;
            Array.Clear(weights);
            int bass = int.MaxValue;
            while (first < notes.Count && notes[first].End <= beatStart && notes[first].Start < beatStart) first++;
            for (int i = first; i < notes.Count && notes[i].Start < beatEnd; i++)
            {
                var n = notes[i];
                long overlap = Math.Min(n.End, beatEnd) - Math.Max(n.Start, beatStart);
                if (overlap <= 0) continue;
                weights[n.Pitch % 12] += overlap * (0.5 + n.Velocity / 254.0);
                if (n.Pitch < bass) bass = n.Pitch;
            }
            string? chord = Best(weights, bass == int.MaxValue ? -1 : bass % 12);
            if (chord is not null && chord != last)
            {
                result.Add(new ChordSymbol(beatStart, chord, Detected: true));
                last = chord;
            }
            beatStart = beatEnd;
        }
        return result;
    }

    /// <summary>Best chord for a 12-bin pitch-class weight vector, or null when too ambiguous.</summary>
    public static string? Best(double[] weights, int bassPc)
    {
        double total = weights.Sum();
        if (total <= 0) return null;
        int present = 0;
        for (int i = 0; i < 12; i++) if (weights[i] / total > 0.05) present++;
        if (present < 2) return null;

        double bestScore = double.NegativeInfinity;
        string? best = null;
        for (int root = 0; root < 12; root++)
        {
            foreach (var (suffix, intervals, bias) in Templates)
            {
                double inside = 0, missing = 0;
                foreach (int iv in intervals)
                {
                    double w = weights[(root + iv) % 12] / total;
                    inside += w;
                    if (w < 0.02) missing += iv == 0 ? 0.25 : 0.18;
                }
                double outside = 1 - inside;
                double score = inside - outside - missing + bias;
                if (bassPc == root) score += 0.08;
                if (score > bestScore) { bestScore = score; best = ChordParser.PitchClassName(root) + suffix; }
            }
        }
        return bestScore >= 0.35 ? best : null;
    }

    private readonly record struct NoteSpan(long Start, long End, int Pitch, int Velocity);

    private static List<NoteSpan> CollectNotes(IReadOnlyList<MidiEvent> events, int drumChannel)
    {
        var open = new Dictionary<int, Stack<(long Tick, int Vel)>>();
        var list = new List<NoteSpan>();
        foreach (var e in events)
        {
            if (!e.IsChannelMessage || e.Channel == drumChannel) continue;
            int key = e.Channel * 128 + e.Data1;
            if (e.IsNoteOn)
            {
                if (!open.TryGetValue(key, out var st)) open[key] = st = new Stack<(long, int)>();
                st.Push((e.Tick, e.Data2));
            }
            else if (e.IsNoteOff && open.TryGetValue(key, out var st) && st.Count > 0)
            {
                var (t, v) = st.Pop();
                if (e.Tick > t) list.Add(new NoteSpan(t, e.Tick, e.Data1, v));
            }
        }
        return list;
    }
}
