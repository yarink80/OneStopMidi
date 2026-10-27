using System.Collections.Concurrent;
using OneStopMidi.Core.Midi;
using OneStopMidi.Core.Output;

namespace OneStopMidi.Core;

/// <summary>
/// MIDI file playback engine. A dedicated high-priority thread schedules events against a Stopwatch; all public
/// members are thread-safe (one internal lock) and events are raised on a separate background event thread.
/// When no output device can be opened the engine keeps running silently.
/// </summary>
public sealed partial class PlayerEngine : IPlayerEngine
{
    private const double PositionInterval = 1.0 / 30;

    private readonly object _sync = new();
    private readonly IMidiOutputProvider _provider;
    private readonly IPlaybackClock _clock;
    private readonly Thread? _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly BlockingCollection<Action>? _eventQueue;
    private readonly Thread? _eventThread;
    private readonly List<Action> _pending = new();
    private volatile bool _disposed;

    // song
    private MidiSong? _song;
    private SongLoadOptions? _loadOptions;
    private MidiEvent[] _events = Array.Empty<MidiEvent>();
    private int _nextIndex;
    private TempoMap _map = TempoMap.Constant(480, 120);
    private double? _baseTempoOverride;
    private double _speed = 1;
    private int _tempoPercent;
    private int _transpose;

    // transport
    private PlaybackState _state = PlaybackState.Stopped;
    private double _position;
    private double _anchorMicros, _anchorWall;
    private double _lastPositionRaise = double.NegativeInfinity;
    private long _nextClickTick;
    private (int Port, int Channel, int Note)? _clickSounding;
    private bool _loopEnabled;
    private long _leftLocator, _rightLocator;

    /// <summary>Uses the WinMM output devices (device 0 selected by default).</summary>
    public PlayerEngine() : this(new WinMmOutputProvider()) { }

    /// <summary>Uses a custom output provider (e.g. <see cref="RecordingMidiOutputProvider"/>).</summary>
    public PlayerEngine(IMidiOutputProvider provider) : this(provider, new StopwatchClock(), realtime: true) { }

    /// <param name="realtime">False for deterministic tests: no threads, events raised synchronously, call <see cref="Pump"/>.</param>
    internal PlayerEngine(IMidiOutputProvider provider, IPlaybackClock clock, bool realtime)
    {
        _provider = provider;
        _clock = clock;
        DrumChannels[9] = true;
        for (int ch = 0; ch < 16; ch++) Routes[ch] = new ChannelRoute(0, ch);
        ResetMixerValues();
        OpenPorts(new[] { 0 });

        if (realtime)
        {
            _eventQueue = new BlockingCollection<Action>();
            _eventThread = new Thread(EventThreadMain) { IsBackground = true, Name = "OneStopMidi events" };
            _eventThread.Start();
            _thread = new Thread(PlaybackThreadMain) { IsBackground = true, Name = "OneStopMidi playback", Priority = ThreadPriority.Highest };
            _thread.Start();
        }
    }

    // ---- events ----
    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? SongEnded;
    public event EventHandler<int>? ChannelsChanged;
    public event EventHandler? SongChanged;

    private void QueueStateChanged() => _pending.Add(() => StateChanged?.Invoke(this, EventArgs.Empty));
    private void QueuePositionChanged() => _pending.Add(() => PositionChanged?.Invoke(this, EventArgs.Empty));
    private void QueueSongEnded() => _pending.Add(() => SongEnded?.Invoke(this, EventArgs.Empty));
    private void QueueSongChanged() => _pending.Add(() => SongChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>Raises queued events outside the lock (on the event thread, or inline in test mode).</summary>
    private void Flush()
    {
        Action[] items;
        lock (_sync)
        {
            FlushChannelChanges();
            if (_pending.Count == 0) return;
            if (_eventQueue is not null)
            {
                // Enqueue under the lock so events from different threads keep their order.
                foreach (var a in _pending)
                {
                    try { _eventQueue.Add(a); } catch (InvalidOperationException) { break; /* disposed */ }
                }
                _pending.Clear();
                return;
            }
            items = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var a in items) SafeInvoke(a);
    }

    private static void SafeInvoke(Action a)
    {
        try { a(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OneStopMidi event handler failed: {ex}"); }
    }

    private void EventThreadMain()
    {
        try
        {
            foreach (var a in _eventQueue!.GetConsumingEnumerable()) SafeInvoke(a);
        }
        catch (ObjectDisposedException) { }
    }

    private void Run(Action body)
    {
        lock (_sync)
        {
            if (_disposed) return;
            body();
        }
        Flush();
        _wake.Set();
    }

    // ---- song ----
    public IMidiSong? Song
    {
        get { lock (_sync) return _song; }
    }

    /// <summary>The loaded song with its full model (tracks, maps), or null.</summary>
    public MidiSong? CurrentSong
    {
        get { lock (_sync) return _song; }
    }

    /// <exception cref="IOException">File cannot be read.</exception>
    /// <exception cref="InvalidDataException">Not a MIDI file.</exception>
    public void Load(string path, SongLoadOptions? options = null)
    {
        var song = MidiSong.Load(path, options);
        Run(() => LoadCore(song, options));
    }

    /// <summary>Loads an already-parsed song.</summary>
    public void Load(MidiSong song, SongLoadOptions? options = null) => Run(() => LoadCore(song, options));

    public void Unload() => Run(() =>
    {
        SilenceAll();
        _state = PlaybackState.Stopped;
        _song = null;
        _events = Array.Empty<MidiEvent>();
        _position = 0;
        _nextIndex = 0;
        _undo.Clear();
        _redo.Clear();
        ResetMixerValues();
        MarkAllChannelsChanged();
        QueueSongChanged();
        QueueStateChanged();
        QueuePositionChanged();
    });

    private void LoadCore(MidiSong song, SongLoadOptions? options)
    {
        SilenceAll();
        _state = PlaybackState.Stopped;
        _song = song;
        _loadOptions = options;
        _baseTempoOverride = null;
        _events = song.Events.ToArray();
        _map = song.TempoMap;
        _position = 0;
        _nextIndex = 0;
        _leftLocator = Math.Clamp(song.StoredLeftLocator ?? 0, 0, song.LengthTicks);
        _rightLocator = Math.Clamp(song.StoredRightLocator ?? song.LengthTicks, 0, song.LengthTicks);
        _undo.Clear();
        _redo.Clear();
        InitMixerFromSong();
        QueueSongChanged();
        QueueStateChanged();
        QueuePositionChanged();
    }

    // ---- transport ----
    public PlaybackState State
    {
        get { lock (_sync) return _state; }
    }

    public long PositionTicks
    {
        get { lock (_sync) return CurrentTickRounded(); }
    }

    /// <summary>Current tick as an integer, tolerant of floating-point error just below a tick boundary.</summary>
    private long CurrentTickRounded() => (long)Math.Floor(CurrentTick() + 1e-6);

    public MusicalPosition Position
    {
        get
        {
            lock (_sync) return _song?.ToMusical(CurrentTickRounded()) ?? new MusicalPosition(1, 1, 0);
        }
    }

    /// <summary>Time at the current position, at the effective tempo (base tempo override and TempoPercent applied).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            lock (_sync)
            {
                if (_song is null) return TimeSpan.Zero;
                return TimeSpan.FromTicks((long)(_map.TickToMicroseconds(CurrentTick()) / _speed * 10));
            }
        }
    }

    private double CurrentTick()
    {
        if (_song is null) return 0;
        double t = _state == PlaybackState.Playing ? TickAtWall(_clock.Seconds) : _position;
        return Math.Clamp(t, 0, _song.LengthTicks);
    }

    private double TickAtWall(double wall) => _map.MicrosecondsToTick(_anchorMicros + (wall - _anchorWall) * 1e6 * _speed);

    private double WallAtTick(double tick) => _anchorWall + (_map.TickToMicroseconds(tick) - _anchorMicros) / (1e6 * _speed);

    private void Anchor(double tick, double wall)
    {
        _anchorMicros = _map.TickToMicroseconds(tick);
        _anchorWall = wall;
    }

    public void Play() => Run(() =>
    {
        if (_song is null) return;
        if (_state == PlaybackState.Playing) SilenceAll();
        var (loopOn, start, _) = LoopRange();
        _state = PlaybackState.Playing;
        StartAt(loopOn ? start : 0, _clock.Seconds, chase: true);
        QueueStateChanged();
        QueuePositionChanged();
    });

    public void Stop() => Run(() =>
    {
        if (_state != PlaybackState.Stopped)
        {
            _position = CurrentTick();
            SilenceAll();
            _state = PlaybackState.Stopped;
            QueueStateChanged();
        }
        else
        {
            _position = 0;
        }
        QueuePositionChanged();
    });

    public void PauseContinue() => Run(() =>
    {
        if (_song is null) return;
        if (_state == PlaybackState.Playing)
        {
            _position = CurrentTick();
            SilenceAll();
            _state = PlaybackState.Paused;
        }
        else
        {
            double from = _position >= _song.LengthTicks && !_loopEnabled ? 0 : _position;
            _state = PlaybackState.Playing;
            StartAt(from, _clock.Seconds, chase: true);
        }
        QueueStateChanged();
        QueuePositionChanged();
    });

    public void Seek(long tick) => Run(() => SeekCore(tick));

    private void SeekCore(long tick)
    {
        if (_song is null) return;
        tick = Math.Clamp(tick, 0, _song.LengthTicks);
        if (_state == PlaybackState.Playing)
        {
            SilenceAll();
            StartAt(tick, _clock.Seconds, chase: true);
        }
        else
        {
            _position = tick;
        }
        QueuePositionChanged();
    }

    public void SeekBars(int deltaBars) => Run(() =>
    {
        if (_song is null) return;
        var m = _song.ToMusical(CurrentTickRounded());
        int bar = Math.Max(1, m.Bar + deltaBars);
        SeekCore(_song.FromMusical(new MusicalPosition(bar, 1, 0)));
    });

    /// <summary>Starts (or restarts) playback bookkeeping at <paramref name="tick"/>.</summary>
    private void StartAt(double tick, double wall, bool chase)
    {
        _position = tick;
        _nextIndex = LowerBound((long)Math.Ceiling(tick));
        if (chase && ChaseEvents) Chase();
        Anchor(tick, wall);
        _nextClickTick = _song?.TimeSignatureMap.NextBeatAtOrAfter((long)Math.Ceiling(tick)) ?? 0;
        _lastPositionRaise = double.NegativeInfinity;
    }

    private int LowerBound(long tick)
    {
        int lo = 0, hi = _events.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_events[mid].Tick < tick) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private (bool On, long Start, long End) LoopRange()
    {
        if (!_loopEnabled || _song is null) return (false, 0, 0);
        if (_rightLocator > _leftLocator) return (true, _leftLocator, _rightLocator);
        return _song.LengthTicks > 0 ? (true, 0, _song.LengthTicks) : (false, 0, 0);
    }

    // ---- playback thread ----
    private void PlaybackThreadMain()
    {
        bool period = false;
        try { period = WinMmNative.timeBeginPeriod(1) == 0; } catch (Exception) { /* not available */ }
        try
        {
            while (!_disposed)
            {
                double wait;
                lock (_sync) wait = _disposed ? 0 : PumpCore();
                Flush();
                if (_disposed) break;
                if (double.IsPositiveInfinity(wait)) _wake.WaitOne(500);
                else if (wait > 0.003) _wake.WaitOne(TimeSpan.FromSeconds(wait - 0.0015));
                else if (wait > 0.0005) Thread.Sleep(0);
                else if (wait > 0) Thread.SpinWait(64);
            }
        }
        finally
        {
            if (period) try { WinMmNative.timeEndPeriod(1); } catch (Exception) { }
        }
    }

    /// <summary>Processes due events at the current clock time (test hook for non-realtime engines).</summary>
    internal void Pump()
    {
        lock (_sync) PumpCore();
        Flush();
    }

    /// <summary>Dispatches everything due; returns seconds until the next thing to do (∞ when idle).</summary>
    private double PumpCore()
    {
        if (_state != PlaybackState.Playing || _song is null) return double.PositiveInfinity;
        double now = _clock.Seconds;
        double tick = TickAtWall(now);
        var (loopOn, loopStart, loopEnd) = LoopRange();

        if (loopOn && tick >= loopEnd)
        {
            DispatchWhile(loopEnd, inclusive: false);
            ClickUntil(loopEnd - 1);
            SilenceAll();
            StartAt(loopStart, now, chase: true);
            QueuePositionChanged();
            _lastPositionRaise = now;
            return 0;
        }

        DispatchWhile(tick, inclusive: true);
        ClickUntil(Math.Min(tick, _song.LengthTicks));

        if (!loopOn && _nextIndex >= _events.Length && tick >= _song.LengthTicks)
        {
            SilenceAll();
            _state = PlaybackState.Stopped;
            _position = 0;
            QueueStateChanged();
            QueuePositionChanged();
            QueueSongEnded();
            return double.PositiveInfinity;
        }

        if (now - _lastPositionRaise >= PositionInterval)
        {
            _lastPositionRaise = now;
            QueuePositionChanged();
        }

        double next = _nextIndex < _events.Length ? _events[_nextIndex].Tick : _song.LengthTicks;
        if (ClickEnabled) next = Math.Min(next, _nextClickTick);
        if (loopOn) next = Math.Min(next, loopEnd);
        double wait = WallAtTick(next) - now;
        wait = Math.Min(wait, _lastPositionRaise + PositionInterval - now);
        return Math.Max(0, wait);
    }

    private void DispatchWhile(double limit, bool inclusive)
    {
        while (_nextIndex < _events.Length)
        {
            var e = _events[_nextIndex];
            if (inclusive ? e.Tick > limit : e.Tick >= limit) break;
            _nextIndex++;
            Dispatch(e);
        }
    }

    private void ClickUntil(double limit)
    {
        if (_song is null) return;
        while (_nextClickTick <= limit)
        {
            if (ClickEnabled)
            {
                ClickOff();
                var (port, dst) = Resolve(9);
                bool downbeat = _song.ToMusical(_nextClickTick) is { Beat: 1, Tick: 0 };
                SendRaw(port, 0x90 | dst, 33, downbeat ? 127 : 90);
                _clickSounding = (port, dst, 33);
            }
            _nextClickTick = _song.TimeSignatureMap.NextBeatAtOrAfter(_nextClickTick + 1);
        }
    }

    private void ClickOff()
    {
        if (_clickSounding is { } c) SendRaw(c.Port, 0x80 | c.Channel, c.Note, 0);
        _clickSounding = null;
    }

    // ---- tempo / pitch ----
    public double CurrentBpm
    {
        get { lock (_sync) return _map.BpmAt(CurrentTickRounded()); }
    }

    public double EffectiveBpm
    {
        get { lock (_sync) return _map.BpmAt(CurrentTickRounded()) * _speed; }
    }

    public bool CanSetBaseTempo
    {
        get { lock (_sync) return _song is { TempoEventCount: <= 1 }; }
    }

    public double? BaseTempoOverride
    {
        get { lock (_sync) return _baseTempoOverride; }
        set => Run(() =>
        {
            if (_song is null || (value.HasValue && _song.TempoEventCount > 1)) return;
            double tick = CurrentTick();
            _baseTempoOverride = value is { } v ? Math.Clamp(v, 10, 1000) : null;
            RebuildTempoMap();
            if (_state == PlaybackState.Playing) Anchor(tick, _clock.Seconds);
            QueuePositionChanged();
        });
    }

    private void RebuildTempoMap()
    {
        if (_song is null) return;
        if (_baseTempoOverride.HasValue && _song.TempoEventCount > 1) _baseTempoOverride = null;
        _map = _baseTempoOverride is { } bpm ? TempoMap.Constant(_song.Ppq, bpm) : _song.TempoMap;
    }

    public int TempoPercent
    {
        get { lock (_sync) return _tempoPercent; }
        set => Run(() =>
        {
            double tick = CurrentTick();
            _tempoPercent = Math.Clamp(value, -90, 200);
            _speed = 1 + _tempoPercent / 100.0;
            if (_state == PlaybackState.Playing) Anchor(tick, _clock.Seconds);
            QueuePositionChanged();
        });
    }

    public int Transpose
    {
        get { lock (_sync) return _transpose; }
        set { lock (_sync) _transpose = Math.Clamp(value, -24, 24); }
    }

    public bool[] DrumChannels { get; } = new bool[16];

    // ---- loop / locators ----
    public bool LoopEnabled
    {
        get { lock (_sync) return _loopEnabled; }
        set => Run(() => _loopEnabled = value);
    }

    public long LeftLocator
    {
        get { lock (_sync) return _leftLocator; }
        set => Run(() => _leftLocator = ClampToSong(value));
    }

    public long RightLocator
    {
        get { lock (_sync) return _rightLocator; }
        set => Run(() => _rightLocator = ClampToSong(value));
    }

    private long ClampToSong(long tick) => Math.Clamp(tick, 0, _song?.LengthTicks ?? long.MaxValue);

    // ---- options ----
    public bool ChaseEvents { get; set; } = true;
    public bool ClickEnabled { get; set; }

    // ---- dispose ----
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _wake.Set();
        if (_thread is not null && Thread.CurrentThread != _thread) _thread.Join(2000);
        lock (_sync)
        {
            SilenceAll();
            ClosePorts();
            _state = PlaybackState.Stopped;
        }
        if (_eventQueue is not null)
        {
            _eventQueue.CompleteAdding();
            if (Thread.CurrentThread != _eventThread) _eventThread?.Join(1000);
        }
    }
}
