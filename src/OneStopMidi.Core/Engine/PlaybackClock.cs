using System.Diagnostics;

namespace OneStopMidi.Core;

/// <summary>Monotonic time source for the playback engine (seconds).</summary>
internal interface IPlaybackClock
{
    double Seconds { get; }
}

internal sealed class StopwatchClock : IPlaybackClock
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    public double Seconds => _sw.Elapsed.TotalSeconds;
}

/// <summary>Clock advanced by hand (deterministic tests).</summary>
internal sealed class ManualClock : IPlaybackClock
{
    public double Seconds { get; set; }
    public void Advance(double seconds) => Seconds += seconds;
}
