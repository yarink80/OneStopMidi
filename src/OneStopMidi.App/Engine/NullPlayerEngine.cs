using System.Diagnostics;
using System.Text;
using OneStopMidi.Core;

namespace OneStopMidi.App.Engine;

/// <summary>
/// Silent stand-in for the real engine: reads only the SMF header, simulates a 4/4 clock at the song tempo and
/// raises the same events as the real engine (on a background thread). Used until OneStopMidi.Core ships
/// <c>PlayerEngine</c>, and for UI testing.
/// </summary>
public sealed class NullPlayerEngine : IPlayerEngine
{
    private readonly object _lock = new();
    private readonly Timer _timer;
    private readonly Stopwatch _clock = new();
    private TimeSpan _lastTick;
    private SimulatedSong? _song;
    private PlaybackState _state = PlaybackState.Stopped;
    private double _pos;
    private int _tempoPercent;
    private int _transpose;
    private double? _baseTempoOverride;
    private readonly ChannelState[] _channels = new ChannelState[16];
    private bool _disposed;

    public NullPlayerEngine()
    {
        for (var i = 0; i < 16; i++) _channels[i] = DefaultChannel(i, hasNotes: false);
        DrumChannels[9] = true;
        _timer = new Timer(OnTimer, null, 33, 33);
    }

    private ChannelState DefaultChannel(int ch, bool hasNotes) => new(
        ch, 0, 0, 0, 100, 64, 40, 0, false, false, hasNotes, ch == 9, null,
        ch == 9 ? GeneralMidi.DrumKitName(0) : GeneralMidi.ProgramName(0));

    // ------------------------------------------------------------------ song

    public IMidiSong? Song => _song;

    public void Load(string path, SongLoadOptions? options = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found.", path);
        var song = SimulatedSong.FromFile(path);
        lock (_lock)
        {
            _state = PlaybackState.Stopped;
            _clock.Reset();
            _song = song;
            _pos = 0;
            _baseTempoOverride = null;
            LeftLocator = 0;
            RightLocator = song.LengthTicks;
            var rnd = new Random(path.GetHashCode());
            for (var i = 0; i < 16; i++)
            {
                var has = i == 9 || rnd.NextDouble() < 0.45;
                var prog = i == 9 ? 0 : rnd.Next(0, 128);
                _channels[i] = DefaultChannel(i, has) with
                {
                    Program = prog,
                    InstrumentName = i == 9 ? GeneralMidi.DrumKitName(prog) : GeneralMidi.ProgramName(prog),
                    TrackName = has ? (i == 9 ? "Drums" : $"Track {i + 1}") : null,
                    Volume = has ? 90 + rnd.Next(0, 38) : 100,
                    Pan = has ? rnd.Next(24, 104) : 64,
                };
            }
        }
        SongChanged?.Invoke(this, EventArgs.Empty);
        ChannelsChanged?.Invoke(this, -1);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Unload()
    {
        lock (_lock)
        {
            _song = null;
            _state = PlaybackState.Stopped;
            _pos = 0;
        }
        SongChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ transport

    public PlaybackState State => _state;
    public long PositionTicks => (long)_pos;
    public MusicalPosition Position => _song?.ToMusical(PositionTicks) ?? new MusicalPosition(1, 1, 0);
    public TimeSpan Elapsed => _song?.TickToTime(PositionTicks) ?? TimeSpan.Zero;

    public void Play()
    {
        if (_song is null) return;
        lock (_lock)
        {
            _pos = LoopEnabled ? LeftLocator : 0;
            StartClock();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_state == PlaybackState.Stopped) _pos = 0;
            _state = PlaybackState.Stopped;
            _clock.Stop();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PauseContinue()
    {
        if (_song is null) return;
        lock (_lock)
        {
            if (_state == PlaybackState.Playing)
            {
                _state = PlaybackState.Paused;
                _clock.Stop();
            }
            else
            {
                StartClock();
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartClock()
    {
        _state = PlaybackState.Playing;
        _clock.Restart();
        _lastTick = TimeSpan.Zero;
    }

    public void Seek(long tick)
    {
        if (_song is null) return;
        lock (_lock) _pos = Math.Clamp(tick, 0, _song.LengthTicks);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SeekBars(int deltaBars)
    {
        if (_song is null) return;
        var p = _song.ToMusical(PositionTicks);
        var bar = Math.Max(1, p.Bar + deltaBars);
        Seek(_song.FromMusical(new MusicalPosition(bar, 1, 0)));
    }

    private void OnTimer(object? _)
    {
        var raiseEnd = false;
        var raisePos = false;
        lock (_lock)
        {
            if (_disposed || _song is null || _state != PlaybackState.Playing) return;
            var now = _clock.Elapsed;
            var dt = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            _pos += dt * EffectiveBpm / 60.0 * _song.Ppq;
            raisePos = true;
            if (LoopEnabled && RightLocator > LeftLocator && _pos >= RightLocator)
            {
                _pos = LeftLocator;
            }
            else if (_pos >= _song.LengthTicks)
            {
                _pos = 0;
                _state = PlaybackState.Stopped;
                _clock.Stop();
                raiseEnd = true;
            }
        }
        if (raisePos) PositionChanged?.Invoke(this, EventArgs.Empty);
        if (raiseEnd)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            SongEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ tempo / pitch

    public double CurrentBpm => _baseTempoOverride ?? _song?.Tempos.FirstOrDefault()?.Bpm ?? 120;
    public double EffectiveBpm => CurrentBpm * (100 + _tempoPercent) / 100.0;
    public bool CanSetBaseTempo => _song is not null && _song.Tempos.Count <= 1;

    public double? BaseTempoOverride
    {
        get => _baseTempoOverride;
        set
        {
            if (!CanSetBaseTempo) return;
            _baseTempoOverride = value is double v ? Math.Clamp(v, 10, 500) : null;
        }
    }

    public int TempoPercent
    {
        get => _tempoPercent;
        set => _tempoPercent = Math.Clamp(value, -90, 200);
    }

    public int Transpose
    {
        get => _transpose;
        set => _transpose = Math.Clamp(value, -24, 24);
    }

    public bool[] DrumChannels { get; } = new bool[16];

    // ------------------------------------------------------------------ loop / options

    public bool LoopEnabled { get; set; }
    public long LeftLocator { get; set; }
    public long RightLocator { get; set; }
    public bool ChaseEvents { get; set; } = true;
    public bool ClickEnabled { get; set; }

    // ------------------------------------------------------------------ mixer

    public IReadOnlyList<ChannelState> Channels
    {
        get { lock (_lock) return _channels.ToArray(); }
    }

    private void Update(int ch, Func<ChannelState, ChannelState> f)
    {
        if (ch is < 0 or > 15) return;
        lock (_lock) _channels[ch] = f(_channels[ch]);
        ChannelsChanged?.Invoke(this, ch);
    }

    public void SetVolume(int channel, int value) => Update(channel, c => c with { Volume = Math.Clamp(value, 0, 127) });
    public void SetPan(int channel, int value) => Update(channel, c => c with { Pan = Math.Clamp(value, 0, 127) });
    public void SetReverb(int channel, int value) => Update(channel, c => c with { Reverb = Math.Clamp(value, 0, 127) });
    public void SetChorus(int channel, int value) => Update(channel, c => c with { Chorus = Math.Clamp(value, 0, 127) });
    public void SetMute(int channel, bool muted) => Update(channel, c => c with { Muted = muted });

    public void SetAllVolumes(int value, bool relative)
    {
        lock (_lock)
        {
            for (var i = 0; i < 16; i++)
            {
                var v = relative ? _channels[i].Volume + value : value;
                _channels[i] = _channels[i] with { Volume = Math.Clamp(v, 0, 127) };
            }
        }
        ChannelsChanged?.Invoke(this, -1);
    }

    public void SetProgram(int channel, int program, int? bankMsb = null, int? bankLsb = null) =>
        Update(channel, c => c with
        {
            Program = Math.Clamp(program, 0, 127),
            BankMsb = bankMsb ?? c.BankMsb,
            BankLsb = bankLsb ?? c.BankLsb,
            InstrumentName = c.IsDrumChannel ? GeneralMidi.DrumKitName(program) : GeneralMidi.ProgramName(program),
        });

    public void SetSolo(int channel, bool soloed, bool additive)
    {
        if (channel is < 0 or > 15) return;
        lock (_lock)
        {
            if (!additive)
                for (var i = 0; i < 16; i++) _channels[i] = _channels[i] with { Soloed = false };
            _channels[channel] = _channels[channel] with { Soloed = soloed };
        }
        ChannelsChanged?.Invoke(this, -1);
    }

    public void FullVolume() => SetAllVolumes(127, relative: false);

    public void ResetDevices()
    {
        lock (_lock)
            for (var i = 0; i < 16; i++)
                _channels[i] = _channels[i] with { Volume = 100, Pan = 64, Reverb = 40, Chorus = 0 };
        ChannelsChanged?.Invoke(this, -1);
    }

    // ------------------------------------------------------------------ devices

    public IReadOnlyList<MidiDeviceInfo> GetOutputDevices() => [new MidiDeviceInfo(0, "Simulated output (silent)")];
    public IReadOnlyList<int> SelectedOutputDevices { get; set; } = [0];
    public ChannelRoute[] Routes { get; } = Enumerable.Range(0, 16).Select(i => new ChannelRoute(0, i)).ToArray();

    // ------------------------------------------------------------------ editing (no-ops in the simulation)

    public bool CanUndo => false;
    public bool CanRedo => false;
    public void Undo() { }
    public void Redo() { }
    public void CutSelection() { }
    public void CopySelection() { }
    public bool CanPaste => false;
    public void Paste() { }
    public void DeleteSelection() { }
    public void EraseMutedChannels() { }

    public void SetSequenceName(string name)
    {
        if (_song is not null) _song.SequenceName = name;
        SongChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string path, int? format = null)
    {
        if (_song?.FilePath is null) throw new InvalidOperationException("No song loaded.");
        if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(_song.FilePath), StringComparison.OrdinalIgnoreCase))
            File.Copy(_song.FilePath, path, overwrite: true);
    }

    public void RevertToSaved()
    {
        if (_song?.FilePath is { } p) Load(p);
    }

    // ------------------------------------------------------------------ events

    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? SongEnded;
    public event EventHandler<int>? ChannelsChanged;
    public event EventHandler? SongChanged;

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        _timer.Dispose();
    }

    // ------------------------------------------------------------------ simulated song

    private sealed class SimulatedSong : IMidiSong
    {
        public string? FilePath { get; init; }
        public string? SequenceName { get; set; }
        public int Format { get; init; }
        public int Ppq { get; init; } = 480;
        public int TrackCount { get; init; }
        public long LengthTicks { get; init; }
        public TimeSpan Duration => TickToTime(LengthTicks);
        public bool IsModified => false;
        public IReadOnlyList<TimeSignatureChange> TimeSignatures { get; init; } = [];
        public IReadOnlyList<TempoChange> Tempos { get; init; } = [];
        public IReadOnlyList<LyricSyllable> Lyrics { get; init; } = [];
        public IReadOnlyList<ChordSymbol> Chords { get; init; } = [];
        public IReadOnlyList<string> Warnings { get; init; } = [];

        private int TicksPerBar => Ppq * 4;

        public MusicalPosition ToMusical(long tick)
        {
            tick = Math.Max(0, tick);
            return new MusicalPosition((int)(tick / TicksPerBar) + 1, (int)(tick % TicksPerBar / Ppq) + 1, (int)(tick % Ppq));
        }

        public long FromMusical(MusicalPosition p) =>
            (long)(Math.Max(1, p.Bar) - 1) * TicksPerBar + (long)(Math.Clamp(p.Beat, 1, 4) - 1) * Ppq + Math.Clamp(p.Tick, 0, Ppq - 1);

        public TimeSpan TickToTime(long tick) =>
            TimeSpan.FromSeconds(tick / (double)Ppq * 60.0 / (Tempos.FirstOrDefault()?.Bpm ?? 120));

        public static SimulatedSong FromFile(string path)
        {
            var header = new byte[14];
            int read;
            using (var fs = File.OpenRead(path)) read = fs.Read(header, 0, header.Length);
            if (read < 14 || Encoding.ASCII.GetString(header, 0, 4) != "MThd")
                throw new InvalidDataException("Not a Standard MIDI File (missing MThd header).");
            var format = (header[8] << 8) | header[9];
            var tracks = (header[10] << 8) | header[11];
            var division = (header[12] << 8) | header[13];
            var ppq = division is > 0 and < 0x8000 ? division : 480;
            var size = new FileInfo(path).Length;
            var bars = (int)Math.Clamp(16 + size / 1500, 16, 160);
            var bpm = 90 + Math.Abs(path.GetHashCode() % 60);
            var lyrics = new List<LyricSyllable>();
            var chords = new List<ChordSymbol>();
            if (path.EndsWith(".kar", StringComparison.OrdinalIgnoreCase))
            {
                string[] words = ["Sing", "a", "long", "with", "the", "simu", "la", "ted", "song", "to", "night"];
                string[] chordNames = ["C", "Am", "F", "G7"];
                var w = 0;
                for (var bar = 1; bar < bars; bar++)
                {
                    for (var beat = 0; beat < 4; beat++)
                    {
                        var tick = (long)bar * ppq * 4 + beat * ppq;
                        lyrics.Add(new LyricSyllable(tick, words[w % words.Length] + (beat == 3 ? "" : " "), beat == 0 && bar > 1, beat == 0 && bar % 8 == 1));
                        w++;
                    }
                    if (bar % 2 == 1) chords.Add(new ChordSymbol((long)bar * ppq * 4, chordNames[bar / 2 % chordNames.Length], false));
                }
            }
            return new SimulatedSong
            {
                FilePath = path,
                SequenceName = Path.GetFileNameWithoutExtension(path),
                Format = format,
                Ppq = ppq,
                TrackCount = tracks,
                LengthTicks = (long)bars * ppq * 4,
                TimeSignatures = [new TimeSignatureChange(0, 4, 4)],
                Tempos = [new TempoChange(0, bpm)],
                Lyrics = lyrics,
                Chords = chords,
                Warnings = tracks == 0 ? ["File contains no tracks."] : [],
            };
        }
    }
}
