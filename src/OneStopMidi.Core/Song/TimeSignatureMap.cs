namespace OneStopMidi.Core;

/// <summary>
/// Time signature map converting ticks to bar/beat/tick and back. Bars and beats are 1-based, the tick is the
/// 0-based offset inside the beat, and one beat is a 1/denominator note. A signature change that does not fall
/// on a bar line starts a new bar.
/// </summary>
public sealed class TimeSignatureMap
{
    private readonly Segment[] _segments;

    private readonly record struct Segment(long Tick, int Numerator, int Denominator, int Bar, long BeatTicks)
    {
        public long BarTicks => BeatTicks * Numerator;
    }

    public int Ppq { get; }

    public TimeSignatureMap(int ppq, IEnumerable<TimeSignatureChange> changes)
    {
        Ppq = Math.Max(1, ppq);
        var list = new List<TimeSignatureChange>();
        foreach (var c in changes.Select((c, i) => (c, i)).OrderBy(x => x.c.Tick).ThenBy(x => x.i).Select(x => x.c))
        {
            var cc = c with { Tick = Math.Max(0, c.Tick), Numerator = Math.Max(1, c.Numerator), Denominator = Math.Max(1, c.Denominator) };
            if (list.Count > 0 && list[^1].Tick == cc.Tick) list[^1] = cc;
            else list.Add(cc);
        }
        if (list.Count == 0 || list[0].Tick > 0) list.Insert(0, new TimeSignatureChange(0, 4, 4));

        _segments = new Segment[list.Count];
        int bar = 1;
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                var prev = _segments[i - 1];
                long delta = list[i].Tick - prev.Tick;
                bar = prev.Bar + (int)((delta + prev.BarTicks - 1) / prev.BarTicks);
            }
            long beat = Math.Max(1, (long)Ppq * 4 / list[i].Denominator);
            _segments[i] = new Segment(list[i].Tick, list[i].Numerator, list[i].Denominator, bar, beat);
        }
    }

    public IReadOnlyList<TimeSignatureChange> Changes =>
        _segments.Select(s => new TimeSignatureChange(s.Tick, s.Numerator, s.Denominator)).ToArray();

    public TimeSignatureChange SignatureAt(long tick)
    {
        var s = _segments[SegmentForTick(tick)];
        return new TimeSignatureChange(s.Tick, s.Numerator, s.Denominator);
    }

    /// <summary>Length of one beat in ticks at <paramref name="tick"/>.</summary>
    public long BeatTicksAt(long tick) => _segments[SegmentForTick(tick)].BeatTicks;

    public MusicalPosition ToMusical(long tick)
    {
        if (tick < 0) tick = 0;
        var s = _segments[SegmentForTick(tick)];
        long into = tick - s.Tick;
        long bars = into / s.BarTicks;
        long rem = into % s.BarTicks;
        return new MusicalPosition(s.Bar + (int)bars, (int)(rem / s.BeatTicks) + 1, (int)(rem % s.BeatTicks));
    }

    public long FromMusical(MusicalPosition position)
    {
        int bar = Math.Max(1, position.Bar);
        int idx = 0;
        for (int i = _segments.Length - 1; i >= 0; i--)
            if (_segments[i].Bar <= bar) { idx = i; break; }
        var s = _segments[idx];
        long tick = s.Tick + (long)(bar - s.Bar) * s.BarTicks + (long)(Math.Max(1, position.Beat) - 1) * s.BeatTicks
                    + Math.Max(0, position.Tick);
        return tick;
    }

    /// <summary>Start tick of the bar containing <paramref name="tick"/>.</summary>
    public long BarStart(long tick)
    {
        var m = ToMusical(tick);
        return FromMusical(new MusicalPosition(m.Bar, 1, 0));
    }

    /// <summary>Smallest beat boundary at or after <paramref name="tick"/>.</summary>
    public long NextBeatAtOrAfter(long tick)
    {
        if (tick <= 0) return 0;
        int i = SegmentForTick(tick);
        var s = _segments[i];
        long into = tick - s.Tick;
        long b = s.Tick + (into + s.BeatTicks - 1) / s.BeatTicks * s.BeatTicks;
        if (i + 1 < _segments.Length && b > _segments[i + 1].Tick) b = _segments[i + 1].Tick;
        return b;
    }

    private int SegmentForTick(long tick)
    {
        int lo = 0, hi = _segments.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_segments[mid].Tick <= tick) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
