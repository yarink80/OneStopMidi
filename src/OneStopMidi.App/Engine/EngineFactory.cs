using OneStopMidi.Core;

namespace OneStopMidi.App.Engine;

/// <summary>
/// THE single place where the app obtains its <see cref="IPlayerEngine"/>: the real WinMM engine, or the in-app
/// simulated engine when <c>ONESTOPMIDI_ENGINE=simulated</c> is set or the real engine cannot be created.
/// </summary>
public static class EngineFactory
{
    /// <summary>Why the last <see cref="Create"/> fell back to the simulated engine (null when the real one was used).</summary>
    public static string? FallbackReason { get; private set; }

    public static IPlayerEngine Create()
    {
        FallbackReason = null;
        if (string.Equals(Environment.GetEnvironmentVariable("ONESTOPMIDI_ENGINE"), "simulated", StringComparison.OrdinalIgnoreCase))
        {
            FallbackReason = "ONESTOPMIDI_ENGINE=simulated";
            return new NullPlayerEngine();
        }

        try
        {
            return new PlayerEngine();
        }
        catch (Exception ex)
        {
            FallbackReason = $"Playback engine could not be created: {ex.Message}";
            Common.CrashLog.Write("Engine creation failed", ex);
            return new NullPlayerEngine();
        }
    }

    public static bool IsSimulated(IPlayerEngine engine) => engine is NullPlayerEngine;
}
