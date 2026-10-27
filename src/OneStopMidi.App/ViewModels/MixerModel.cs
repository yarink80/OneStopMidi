using System.Collections.ObjectModel;
using OneStopMidi.App.Common;
using OneStopMidi.Core;

namespace OneStopMidi.App.ViewModels;

/// <summary>The 16 channel strips. Values pushed from the engine do not echo back to it.</summary>
public sealed class MixerModel
{
    public const int DefaultVolume = 100;
    public const int DefaultPan = 64;
    public const int DefaultReverb = 40;
    public const int DefaultChorus = 0;

    public IPlayerEngine Engine { get; }
    public ObservableCollection<ChannelStripModel> Strips { get; } = new();

    /// <summary>Raised when an instrument button is clicked.</summary>
    public event EventHandler<ChannelStripModel>? InstrumentPickRequested;

    private bool _displayTrackNames = true;

    public MixerModel(IPlayerEngine engine)
    {
        Engine = engine;
        for (var i = 0; i < 16; i++) Strips.Add(new ChannelStripModel(this, i));
    }

    public bool DisplayTrackNames
    {
        get => _displayTrackNames;
        set
        {
            _displayTrackNames = value;
            foreach (var s in Strips) s.RaiseLabel();
        }
    }

    public void RefreshFromEngine()
    {
        IReadOnlyList<ChannelState> states;
        try
        {
            states = Engine.Channels;
        }
        catch
        {
            return;
        }
        foreach (var st in states)
            if (st.Channel is >= 0 and < 16) Strips[st.Channel].Apply(st);
    }

    /// <summary>Alt+drag on one fader: move every other fader by the same amount.</summary>
    public void ApplyVolumeDeltaToOthers(int sourceChannel, int delta)
    {
        if (delta == 0) return;
        foreach (var s in Strips)
            if (s.Channel != sourceChannel) s.Volume = Math.Clamp(s.Volume + delta, 0, 127);
    }

    internal void RequestInstrument(ChannelStripModel strip) => InstrumentPickRequested?.Invoke(this, strip);
}

public sealed class ChannelStripModel : ObservableObject
{
    private readonly MixerModel _mixer;
    private bool _syncing;
    private int _volume = MixerModel.DefaultVolume, _pan = MixerModel.DefaultPan, _reverb = MixerModel.DefaultReverb, _chorus, _program;
    private bool _muted, _soloed, _hasNotes, _isDrum;
    private string _instrumentName = "";
    private string? _trackName;

    public ChannelStripModel(MixerModel mixer, int channel)
    {
        _mixer = mixer;
        Channel = channel;
        _isDrum = channel == 9;
    }

    public int Channel { get; }
    public string Number => (Channel + 1).ToString();
    private IPlayerEngine Engine => _mixer.Engine;

    public int Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, Math.Clamp(value, 0, 127)) && !_syncing) Try(() => Engine.SetVolume(Channel, _volume));
        }
    }

    public int Pan
    {
        get => _pan;
        set
        {
            if (Set(ref _pan, Math.Clamp(value, 0, 127)))
            {
                Raise(nameof(PanText));
                if (!_syncing) Try(() => Engine.SetPan(Channel, _pan));
            }
        }
    }

    public string PanText => _pan == 64 ? "C" : _pan < 64 ? $"L{64 - _pan}" : $"R{_pan - 64}";

    public int Reverb
    {
        get => _reverb;
        set
        {
            if (Set(ref _reverb, Math.Clamp(value, 0, 127)) && !_syncing) Try(() => Engine.SetReverb(Channel, _reverb));
        }
    }

    public int Chorus
    {
        get => _chorus;
        set
        {
            if (Set(ref _chorus, Math.Clamp(value, 0, 127)) && !_syncing) Try(() => Engine.SetChorus(Channel, _chorus));
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (Set(ref _muted, value) && !_syncing) Try(() => Engine.SetMute(Channel, value));
        }
    }

    public bool Soloed => _soloed;

    /// <summary>Solo click; Shift = additive (other solos are kept).</summary>
    public void ToggleSolo(bool additive) => Try(() => Engine.SetSolo(Channel, !_soloed, additive));

    public int Program => _program;
    public bool HasNotes => _hasNotes;
    public bool IsDrum => _isDrum;
    public string InstrumentName => _instrumentName;
    public string? TrackName => _trackName;

    public string Label => _mixer.DisplayTrackNames && !string.IsNullOrWhiteSpace(_trackName) ? _trackName! : _instrumentName;

    public string ToolTipText =>
        $"Channel {Channel + 1}{(_isDrum ? " (drums)" : "")}\nInstrument: {_instrumentName} (program {_program + 1})" +
        (string.IsNullOrWhiteSpace(_trackName) ? "" : $"\nTrack: {_trackName}") +
        "\nClick to choose an instrument.";

    internal void RaiseLabel()
    {
        Raise(nameof(Label));
        Raise(nameof(ToolTipText));
    }

    public void PickInstrument() => _mixer.RequestInstrument(this);

    public void Apply(ChannelState s)
    {
        _syncing = true;
        try
        {
            Volume = s.Volume;
            Pan = s.Pan;
            Reverb = s.Reverb;
            Chorus = s.Chorus;
            Muted = s.Muted;
            if (Set(ref _soloed, s.Soloed, nameof(Soloed))) { }
            if (Set(ref _hasNotes, s.HasNotes, nameof(HasNotes))) { }
            if (Set(ref _isDrum, s.IsDrumChannel, nameof(IsDrum))) RaiseLabel();
            if (Set(ref _program, s.Program, nameof(Program))) RaiseLabel();
            if (Set(ref _instrumentName, s.InstrumentName ?? "", nameof(InstrumentName))) RaiseLabel();
            if (Set(ref _trackName, s.TrackName, nameof(TrackName))) RaiseLabel();
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void Try(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Mixer engine call failed", ex);
        }
    }
}
