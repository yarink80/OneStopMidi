namespace OneStopMidi.Core;

/// <summary>Piecewise-constant tempo map converting ticks to microseconds and back.</summary>
public sealed class TempoMap
{
    public const int DefaultMicrosecondsPerQuarter = 500_000;

    private readonly long[] _ticks;
    private readonly double[] _micros;
    private readonly int[] _usPerQuarter;

    public int Ppq { get; }

    /// <param name="ppq">Pulses per quarter note.</param>
    /// <param name="changes">Tempo changes (tick, microseconds per quarter); unsorted input is fine, later entries win on equal ticks.</param>
    public TempoMap(int ppq, IEnumerable<(long Tick, int MicrosecondsPerQuarter)> changes)
    {
        Ppq = Math.Max(1, ppq);
        var list = new List<(long Tick, int Us)>();
        foreach (var c in changes.Select((c, i) => (c, i)).OrderBy(x => x.c.Tick).ThenBy(x => x.i).Select(x => x.c))
        {
            int us = c.MicrosecondsPerQuarter <= 0 ? DefaultMicrosecondsPerQuarter : c.MicrosecondsPerQuarter;
            if (list.Count > 0 && list[^1].Tick == c.Tick) list[^1] = (c.Tick, us);
            else list.Add((Math.Max(0, c.Tick), us));
        }
        if (list.Count == 0 || list[0].Tick > 0) list.Insert(0, (0, DefaultMicrosecondsPerQuarter));

        _ticks = new long[list.Count];
        _micros = new double[list.Count];
        _usPerQuarter = new int[list.Count];
        double t = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) t += (list[i].Tick - list[i - 1].Tick) * (double)list[i - 1].Us / Ppq;
            _ticks[i] = list[i].Tick;
            _micros[i] = t;
            _usPerQuarter[i] = list[i].Us;
        }
    }

    public static TempoMap Constant(int ppq, double bpm) => new(ppq, new[] { (0L, (int)Math.Round(60_000_000.0 / bpm)) });

    /// <summary>Number of segments (tempo changes, including an implicit default at tick 0).</summary>
    public int Count => _ticks.Length;

    public IReadOnlyList<TempoChange> Changes =>
        _ticks.Select((t, i) => new TempoChange(t, 60_000_000.0 / _usPerQuarter[i])).ToArray();

    public double TickToMicroseconds(double tick)
    {
        if (tick <= 0) return tick * _usPerQuarter[0] / Ppq;
        int i = SegmentForTick(tick);
        return _micros[i] + (tick - _ticks[i]) * _usPerQuarter[i] / Ppq;
    }

    public double MicrosecondsToTick(double micros)
    {
        if (micros <= 0) return micros * Ppq / _usPerQuarter[0];
        int lo = 0, hi = _micros.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_micros[mid] <= micros) lo = mid; else hi = mid - 1;
        }
        return _ticks[lo] + (micros - _micros[lo]) * Ppq / _usPerQuarter[lo];
    }

    public TimeSpan TickToTime(long tick) => TimeSpan.FromTicks((long)(TickToMicroseconds(tick) * 10));

    public int MicrosecondsPerQuarterAt(long tick) => _usPerQuarter[SegmentForTick(tick)];

    public double BpmAt(long tick) => 60_000_000.0 / MicrosecondsPerQuarterAt(tick);

    /// <summary>First tempo change strictly after <paramref name="tick"/>, or null.</summary>
    public long? NextChangeAfter(long tick)
    {
        int i = SegmentForTick(tick) + 1;
        return i < _ticks.Length ? _ticks[i] : null;
    }

    private int SegmentForTick(double tick)
    {
        int lo = 0, hi = _ticks.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_ticks[mid] <= tick) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
