// FROZEN CONTRACT between OneStopMidi.Core (engine) and OneStopMidi.App (UI).
// Do not change member signatures without coordinating both sides; adding members is fine.
using System.Text;

namespace OneStopMidi.Core;

/// <summary>1-based bar and beat, 0-based tick inside the beat (display form "1 1 0").</summary>
public readonly record struct MusicalPosition(int Bar, int Beat, int Tick)
{
    public override string ToString() => $"{Bar} {Beat} {Tick}";
}

public enum PlaybackState { Stopped, Playing, Paused }

public sealed record TimeSignatureChange(long Tick, int Numerator, int Denominator);

public sealed record TempoChange(long Tick, double Bpm);

/// <summary>One lyric/text fragment. Text never contains the '/' or '\' line markers; they become flags.</summary>
public sealed record LyricSyllable(long Tick, string Text, bool StartsNewLine, bool StartsNewParagraph);

/// <summary>Chord symbol, either embedded in the file or auto-detected.</summary>
public sealed record ChordSymbol(long Tick, string Name, bool Detected);

public sealed record MidiDeviceInfo(int Id, string Name);

/// <summary>Snapshot of one of the 16 mixer channels (Channel is 0-based, displayed as 1-16).</summary>
public sealed record ChannelState(
    int Channel,
    int Program,
    int BankMsb,
    int BankLsb,
    int Volume,
    int Pan,
    int Reverb,
    int Chorus,
    bool Muted,
    bool Soloed,
    bool HasNotes,
    bool IsDrumChannel,
    string? TrackName,
    string InstrumentName);

/// <summary>Per-channel output routing: which output port (index into SelectedOutputDevices) and which channel.</summary>
public readonly record struct ChannelRoute(int Port, int Channel);

public enum ChordDisplayMode { Off, Embedded, AutoDetect, PreferEmbeddedThenDetect }

/// <summary>A loaded Standard MIDI File (read-only view).</summary>
public interface IMidiSong
{
    string? FilePath { get; }
    string? SequenceName { get; }
    int Format { get; }
    int Ppq { get; }
    int TrackCount { get; }
    long LengthTicks { get; }
    TimeSpan Duration { get; }
    bool IsModified { get; }
    IReadOnlyList<TimeSignatureChange> TimeSignatures { get; }
    IReadOnlyList<TempoChange> Tempos { get; }
    IReadOnlyList<LyricSyllable> Lyrics { get; }
    IReadOnlyList<ChordSymbol> Chords { get; }
    /// <summary>"Missing MIDI event" style warnings collected at load (e.g. no volume/program on a channel).</summary>
    IReadOnlyList<string> Warnings { get; }

    MusicalPosition ToMusical(long tick);
    long FromMusical(MusicalPosition position);
    TimeSpan TickToTime(long tick);
}

public sealed class SongLoadOptions
{
    /// <summary>Encoding for text/lyric meta events (MIDI text is single-byte). Default Windows-1252.</summary>
    public Encoding? TextEncoding { get; init; }
    public ChordDisplayMode ChordMode { get; init; } = ChordDisplayMode.PreferEmbeddedThenDetect;
}

/// <summary>
/// The playback engine. Thread-safe: methods may be called from the UI thread; events are raised on a
/// background thread (the UI must marshal). PositionChanged is throttled to about 30 Hz.
/// </summary>
public interface IPlayerEngine : IDisposable
{
    // ---- song ----
    IMidiSong? Song { get; }
    void Load(string path, SongLoadOptions? options = null);
    void Unload();

    // ---- transport ----
    PlaybackState State { get; }
    long PositionTicks { get; }
    MusicalPosition Position { get; }
    TimeSpan Elapsed { get; }
    /// <summary>Play from bar 1 (or from the left locator when loop is on).</summary>
    void Play();
    /// <summary>Stop; if already stopped, reset position to the start.</summary>
    void Stop();
    /// <summary>Paused/stopped: continue from current position. Playing: pause.</summary>
    void PauseContinue();
    void Seek(long tick);
    /// <summary>Move by whole bars (negative = back), clamped to the song.</summary>
    void SeekBars(int deltaBars);

    // ---- tempo / pitch ----
    /// <summary>Tempo of the song at the current position (before TempoPercent).</summary>
    double CurrentBpm { get; }
    /// <summary>Effective BPM after TempoPercent.</summary>
    double EffectiveBpm { get; }
    /// <summary>True when the song has at most one tempo event, so BaseTempo may be overridden.</summary>
    bool CanSetBaseTempo { get; }
    /// <summary>Override the single tempo (only when CanSetBaseTempo). Null restores the file's tempo.</summary>
    double? BaseTempoOverride { get; set; }
    /// <summary>Relative tempo change in percent, -90..+200. 0 = original.</summary>
    int TempoPercent { get; set; }
    /// <summary>Semitones, -24..+24. Drum channels are excluded.</summary>
    int Transpose { get; set; }
    /// <summary>Channels (0-based) excluded from transposition. Default: channel 9 (MIDI 10).</summary>
    bool[] DrumChannels { get; }

    // ---- loop / locators ----
    bool LoopEnabled { get; set; }
    long LeftLocator { get; set; }
    long RightLocator { get; set; }

    // ---- options ----
    bool ChaseEvents { get; set; }
    bool ClickEnabled { get; set; }

    // ---- mixer ----
    IReadOnlyList<ChannelState> Channels { get; }
    void SetVolume(int channel, int value);
    /// <summary>Set all channel volumes (master-style) keeping relative offsets when relative is true.</summary>
    void SetAllVolumes(int value, bool relative);
    void SetPan(int channel, int value);
    void SetReverb(int channel, int value);
    void SetChorus(int channel, int value);
    void SetProgram(int channel, int program, int? bankMsb = null, int? bankLsb = null);
    void SetMute(int channel, bool muted);
    /// <summary>Solo a channel. When additive is false, other solos are cleared first.</summary>
    void SetSolo(int channel, bool soloed, bool additive);
    void FullVolume();
    void ResetDevices();

    // ---- devices / routing ----
    IReadOnlyList<MidiDeviceInfo> GetOutputDevices();
    /// <summary>Output ports in use; port index 0 is "A", 1 is "B", ... At least one port.</summary>
    IReadOnlyList<int> SelectedOutputDevices { get; set; }
    /// <summary>16 routes, indexed by source channel.</summary>
    ChannelRoute[] Routes { get; }

    // ---- editing (range = LeftLocator..RightLocator, insertion point = PositionTicks) ----
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Undo();
    void Redo();
    void CutSelection();
    void CopySelection();
    bool CanPaste { get; }
    void Paste();
    void DeleteSelection();
    void EraseMutedChannels();
    void SetSequenceName(string name);
    /// <summary>Save as SMF. format: null = original, 0 or 1 = forced. Locators are stored in a sequencer-specific meta event.</summary>
    void Save(string path, int? format = null);
    void RevertToSaved();

    // ---- events (background thread) ----
    event EventHandler? StateChanged;
    event EventHandler? PositionChanged;
    /// <summary>Raised when playback reaches the end of the song (not on manual Stop, not when looping).</summary>
    event EventHandler? SongEnded;
    /// <summary>Raised when any mixer channel value changes (from the file, from MIDI or from the UI). Arg = channel or -1 for all.</summary>
    event EventHandler<int>? ChannelsChanged;
    event EventHandler? SongChanged;
}
