using OneStopMidi.Core.Midi;
using OneStopMidi.Core.Output;

namespace OneStopMidi.Core;

public sealed partial class PlayerEngine
{
    private readonly record struct Sounding(int Port, int Channel, int Pitch);

    private readonly Sounding?[] _sounding = new Sounding?[16 * 128];
    private int[] _selected = Array.Empty<int>();
    private IMidiOutputPort?[] _ports = Array.Empty<IMidiOutputPort?>();

    // mixer
    private readonly int[] _program = new int[16];
    private readonly int[] _bankMsb = new int[16];
    private readonly int[] _bankLsb = new int[16];
    private readonly int[] _volume = new int[16];
    private readonly int[] _pan = new int[16];
    private readonly int[] _reverb = new int[16];
    private readonly int[] _chorus = new int[16];
    private readonly bool[] _muted = new bool[16];
    private readonly bool[] _soloed = new bool[16];
    private int _changedMask;

    public const int DefaultVolume = 100;
    public const int DefaultPan = 64;
    public const int DefaultReverb = 40;
    public const int DefaultChorus = 0;

    // ---- devices / routing ----
    public IReadOnlyList<MidiDeviceInfo> GetOutputDevices()
    {
        try { return _provider.GetDevices(); }
        catch (Exception) { return Array.Empty<MidiDeviceInfo>(); }
    }

    public IReadOnlyList<int> SelectedOutputDevices
    {
        get { lock (_sync) return _selected.ToArray(); }
        set => Run(() =>
        {
            var ids = value is { Count: > 0 } ? value.ToArray() : new[] { 0 };
            if (ids.SequenceEqual(_selected)) return;
            SilenceAll();
            ClosePorts();
            OpenPorts(ids);
            SendMixerState();
        });
    }

    public ChannelRoute[] Routes { get; } = new ChannelRoute[16];

    /// <summary>Names of the opened ports (null where a device could not be opened), by port index.</summary>
    public IReadOnlyList<string?> OpenPortNames
    {
        get { lock (_sync) return _ports.Select(p => p?.Name).ToArray(); }
    }

    private void OpenPorts(int[] ids)
    {
        _selected = ids;
        _ports = new IMidiOutputPort?[ids.Length];
        var opened = new Dictionary<int, IMidiOutputPort?>();
        for (int i = 0; i < ids.Length; i++)
        {
            if (!opened.TryGetValue(ids[i], out var port))
            {
                try { port = _provider.Open(ids[i]); } catch (Exception) { port = null; }
                opened[ids[i]] = port;
            }
            _ports[i] = port;
        }
    }

    private void ClosePorts()
    {
        foreach (var p in _ports.Where(p => p is not null).Distinct())
        {
            try { p!.Dispose(); } catch (Exception) { }
        }
        _ports = Array.Empty<IMidiOutputPort?>();
    }

    private (int Port, int Channel) Resolve(int sourceChannel)
    {
        var r = Routes[sourceChannel];
        int port = r.Port >= 0 && r.Port < _ports.Length ? r.Port : 0;
        return (port, r.Channel & 0x0F);
    }

    private void SendRaw(int portIndex, int status, int d1, int d2)
    {
        if (portIndex < 0 || portIndex >= _ports.Length) return;
        var port = _ports[portIndex];
        if (port is null) return;
        try { port.SendShort((byte)status, (byte)(d1 & 0x7F), (byte)(d2 & 0x7F)); }
        catch (Exception) { /* device errors must not stop playback */ }
    }

    private void Send(int sourceChannel, int command, int d1, int d2 = 0)
    {
        var (port, dst) = Resolve(sourceChannel);
        SendRaw(port, command | dst, d1, d2);
    }

    private IEnumerable<IMidiOutputPort> DistinctPorts() => _ports.Where(p => p is not null).Distinct()!;

    private void SendSysEx(MidiEvent e)
    {
        var data = e.Data ?? Array.Empty<byte>();
        byte[] msg;
        if (e.Status == 0xF0)
        {
            msg = new byte[data.Length + 1];
            msg[0] = 0xF0;
            data.CopyTo(msg, 1);
        }
        else msg = data;
        if (msg.Length == 0) return;
        foreach (var p in DistinctPorts())
        {
            try { p.SendSysEx(msg); } catch (Exception) { }
        }
    }

    // ---- event dispatch ----
    private void Dispatch(MidiEvent e)
    {
        if (e.IsSysEx) { SendSysEx(e); return; }
        if (!e.IsChannelMessage) return;
        int ch = e.Channel;
        switch (e.Command)
        {
            case 0x90 when e.Data2 > 0:
                NoteOn(ch, e.Data1, e.Data2);
                break;
            case 0x80:
            case 0x90:
                NoteOff(ch, e.Data1, e.Command == 0x80 ? e.Data2 : 0);
                break;
            case 0xA0:
                if (_sounding[ch * 128 + e.Data1] is { } s) SendRaw(s.Port, 0xA0 | s.Channel, s.Pitch, e.Data2);
                break;
            case 0xB0:
                ApplyControllerToMixer(ch, e.Data1, e.Data2);
                if (e.Data1 is 120 or 123) ForgetSounding(ch);
                Send(ch, 0xB0, e.Data1, e.Data2);
                break;
            case 0xC0:
                if (_program[ch] != e.Data1) { _program[ch] = e.Data1; MarkChannelChanged(ch); }
                Send(ch, 0xC0, e.Data1);
                break;
            default:
                Send(ch, e.Command, e.Data1, e.Data2);
                break;
        }
    }

    private void NoteOn(int ch, int note, int velocity)
    {
        if (!IsAudible(ch)) return;
        int pitch = DrumChannels[ch] ? note : note + _transpose;
        if (pitch is < 0 or > 127) return;
        var (port, dst) = Resolve(ch);
        int key = ch * 128 + note;
        if (_sounding[key] is { } old && old != new Sounding(port, dst, pitch))
            SendRaw(old.Port, 0x80 | old.Channel, old.Pitch, 0);
        _sounding[key] = new Sounding(port, dst, pitch);
        SendRaw(port, 0x90 | dst, pitch, velocity);
    }

    private void NoteOff(int ch, int note, int velocity)
    {
        int key = ch * 128 + note;
        if (_sounding[key] is not { } s) return; // never started (muted / out of range) or already off
        _sounding[key] = null;
        SendRaw(s.Port, 0x80 | s.Channel, s.Pitch, velocity);
    }

    private void ForgetSounding(int ch) => Array.Clear(_sounding, ch * 128, 128);

    /// <summary>Note-offs for every sounding note of one source channel.</summary>
    private void SilenceChannel(int ch)
    {
        for (int n = 0; n < 128; n++)
        {
            if (_sounding[ch * 128 + n] is not { } s) continue;
            _sounding[ch * 128 + n] = null;
            SendRaw(s.Port, 0x80 | s.Channel, s.Pitch, 0);
        }
    }

    /// <summary>Note-offs for tracked notes, then sustain off + all notes off on every channel of every port.</summary>
    private void SilenceAll()
    {
        for (int ch = 0; ch < 16; ch++) SilenceChannel(ch);
        ClickOff();
        for (int p = 0; p < _ports.Length; p++)
        {
            if (_ports[p] is null || Array.IndexOf(_ports, _ports[p]) != p) continue;
            for (int ch = 0; ch < 16; ch++)
            {
                SendRaw(p, 0xB0 | ch, 64, 0);
                SendRaw(p, 0xB0 | ch, 123, 0);
            }
        }
    }

    private bool IsAudible(int ch) => !_muted[ch] && (!_soloed.Any(s => s) || _soloed[ch]);

    // ---- chase ----
    /// <summary>Sends the latest bank/program/volume/pan/reverb/chorus (and expression, modulation, pitch bend)
    /// before the current event index; values the file does not set come from the mixer.</summary>
    private void Chase()
    {
        if (_song is null) return;
        var program = new int?[16];
        var msb = new int?[16];
        var lsb = new int?[16];
        var cc = new int?[16, 128];
        var bend = new int?[16];
        for (int i = 0; i < _nextIndex; i++)
        {
            var e = _events[i];
            if (!e.IsChannelMessage) continue;
            int ch = e.Channel;
            switch (e.Command)
            {
                case 0xB0:
                    if (e.Data1 == 0) msb[ch] = e.Data2;
                    else if (e.Data1 == 32) lsb[ch] = e.Data2;
                    else if (e.Data1 is 1 or 7 or 10 or 11 or 91 or 93) cc[ch, e.Data1] = e.Data2;
                    break;
                case 0xC0: program[ch] = e.Data1; break;
                case 0xE0: bend[ch] = e.Data1 | e.Data2 << 7; break;
            }
        }
        for (int ch = 0; ch < 16; ch++)
        {
            bool fileTouches = program[ch].HasValue || msb[ch].HasValue || lsb[ch].HasValue || bend[ch].HasValue
                               || cc[ch, 7].HasValue || cc[ch, 10].HasValue || cc[ch, 91].HasValue || cc[ch, 93].HasValue;
            if (!_song.ChannelHasNotes[ch] && !fileTouches) continue;
            _bankMsb[ch] = msb[ch] ?? _bankMsb[ch];
            _bankLsb[ch] = lsb[ch] ?? _bankLsb[ch];
            _program[ch] = program[ch] ?? _program[ch];
            _volume[ch] = cc[ch, 7] ?? _volume[ch];
            _pan[ch] = cc[ch, 10] ?? _pan[ch];
            _reverb[ch] = cc[ch, 91] ?? _reverb[ch];
            _chorus[ch] = cc[ch, 93] ?? _chorus[ch];
            Send(ch, 0xB0, 0, _bankMsb[ch]);
            Send(ch, 0xB0, 32, _bankLsb[ch]);
            Send(ch, 0xC0, _program[ch]);
            Send(ch, 0xB0, 7, _volume[ch]);
            Send(ch, 0xB0, 10, _pan[ch]);
            Send(ch, 0xB0, 91, _reverb[ch]);
            Send(ch, 0xB0, 93, _chorus[ch]);
            Send(ch, 0xB0, 11, cc[ch, 11] ?? 127);
            Send(ch, 0xB0, 1, cc[ch, 1] ?? 0);
            int b = bend[ch] ?? 8192;
            Send(ch, 0xE0, b & 0x7F, b >> 7);
            MarkChannelChanged(ch);
        }
    }

    /// <summary>Re-sends the mixer state of every channel (after switching devices).</summary>
    private void SendMixerState()
    {
        for (int ch = 0; ch < 16; ch++)
        {
            Send(ch, 0xB0, 0, _bankMsb[ch]);
            Send(ch, 0xB0, 32, _bankLsb[ch]);
            Send(ch, 0xC0, _program[ch]);
            Send(ch, 0xB0, 7, _volume[ch]);
            Send(ch, 0xB0, 10, _pan[ch]);
            Send(ch, 0xB0, 91, _reverb[ch]);
            Send(ch, 0xB0, 93, _chorus[ch]);
        }
    }

    // ---- mixer ----
    private void ResetMixerValues()
    {
        for (int ch = 0; ch < 16; ch++)
        {
            _program[ch] = 0;
            _bankMsb[ch] = 0;
            _bankLsb[ch] = 0;
            _volume[ch] = DefaultVolume;
            _pan[ch] = DefaultPan;
            _reverb[ch] = DefaultReverb;
            _chorus[ch] = DefaultChorus;
        }
    }

    /// <summary>Mixer defaults overlaid with each channel's controller/program events before its first note.</summary>
    private void InitMixerFromSong()
    {
        ResetMixerValues();
        if (_song is null) return;
        var started = new bool[16];
        foreach (var e in _events)
        {
            if (!e.IsChannelMessage || started[e.Channel]) continue;
            if (e.IsNoteOn) { started[e.Channel] = true; continue; }
            if (e.Command == 0xC0) _program[e.Channel] = e.Data1;
            else if (e.Command == 0xB0) ApplyControllerToMixer(e.Channel, e.Data1, e.Data2);
        }
        MarkAllChannelsChanged();
    }

    private void ApplyControllerToMixer(int ch, int controller, int value)
    {
        int[]? target = controller switch
        {
            0 => _bankMsb, 32 => _bankLsb, 7 => _volume, 10 => _pan, 91 => _reverb, 93 => _chorus, _ => null,
        };
        if (target is null || target[ch] == value) return;
        target[ch] = value;
        MarkChannelChanged(ch);
    }

    private void MarkChannelChanged(int ch) => _changedMask |= 1 << ch;
    private void MarkAllChannelsChanged() => _changedMask = 0xFFFF;

    private void FlushChannelChanges()
    {
        int mask = _changedMask;
        if (mask == 0) return;
        _changedMask = 0;
        int arg = (mask & (mask - 1)) == 0 ? System.Numerics.BitOperations.TrailingZeroCount(mask) : -1;
        _pending.Add(() => ChannelsChanged?.Invoke(this, arg));
    }

    public IReadOnlyList<ChannelState> Channels
    {
        get
        {
            lock (_sync)
            {
                var list = new ChannelState[16];
                for (int ch = 0; ch < 16; ch++)
                {
                    bool drum = DrumChannels[ch];
                    list[ch] = new ChannelState(ch, _program[ch], _bankMsb[ch], _bankLsb[ch], _volume[ch], _pan[ch],
                        _reverb[ch], _chorus[ch], _muted[ch], _soloed[ch], _song?.ChannelHasNotes[ch] ?? false, drum,
                        _song?.ChannelTrackNames[ch], GmNames.Name(_program[ch], drum));
                }
                return list;
            }
        }
    }

    private static bool ValidChannel(int ch) => ch is >= 0 and < 16;

    public void SetVolume(int channel, int value) => Run(() =>
    {
        if (!ValidChannel(channel)) return;
        _volume[channel] = Math.Clamp(value, 0, 127);
        Send(channel, 0xB0, 7, _volume[channel]);
        MarkChannelChanged(channel);
    });

    public void SetAllVolumes(int value, bool relative) => Run(() =>
    {
        value = Math.Clamp(value, 0, 127);
        int delta = value - _volume.Max();
        for (int ch = 0; ch < 16; ch++)
        {
            _volume[ch] = relative ? Math.Clamp(_volume[ch] + delta, 0, 127) : value;
            Send(ch, 0xB0, 7, _volume[ch]);
        }
        MarkAllChannelsChanged();
    });

    public void SetPan(int channel, int value) => SetController(channel, 10, _pan, value);
    public void SetReverb(int channel, int value) => SetController(channel, 91, _reverb, value);
    public void SetChorus(int channel, int value) => SetController(channel, 93, _chorus, value);

    private void SetController(int channel, int controller, int[] target, int value) => Run(() =>
    {
        if (!ValidChannel(channel)) return;
        target[channel] = Math.Clamp(value, 0, 127);
        Send(channel, 0xB0, controller, target[channel]);
        MarkChannelChanged(channel);
    });

    public void SetProgram(int channel, int program, int? bankMsb = null, int? bankLsb = null) => Run(() =>
    {
        if (!ValidChannel(channel)) return;
        if (bankMsb is { } m) { _bankMsb[channel] = Math.Clamp(m, 0, 127); Send(channel, 0xB0, 0, _bankMsb[channel]); }
        if (bankLsb is { } l) { _bankLsb[channel] = Math.Clamp(l, 0, 127); Send(channel, 0xB0, 32, _bankLsb[channel]); }
        _program[channel] = Math.Clamp(program, 0, 127);
        Send(channel, 0xC0, _program[channel]);
        MarkChannelChanged(channel);
    });

    public void SetMute(int channel, bool muted) => Run(() =>
    {
        if (!ValidChannel(channel)) return;
        _muted[channel] = muted;
        if (!IsAudible(channel)) SilenceChannel(channel);
        MarkChannelChanged(channel);
    });

    public void SetSolo(int channel, bool soloed, bool additive) => Run(() =>
    {
        if (!ValidChannel(channel)) return;
        if (!additive) Array.Clear(_soloed);
        _soloed[channel] = soloed;
        for (int ch = 0; ch < 16; ch++) if (!IsAudible(ch)) SilenceChannel(ch);
        MarkAllChannelsChanged();
    });

    public void FullVolume() => Run(() =>
    {
        for (int ch = 0; ch < 16; ch++)
        {
            _volume[ch] = 127;
            Send(ch, 0xB0, 7, 127);
        }
        MarkAllChannelsChanged();
    });

    public void ResetDevices() => Run(() =>
    {
        for (int ch = 0; ch < 16; ch++) SilenceChannel(ch);
        ClickOff();
        for (int p = 0; p < _ports.Length; p++)
        {
            if (_ports[p] is null || Array.IndexOf(_ports, _ports[p]) != p) continue;
            for (int ch = 0; ch < 16; ch++)
            {
                SendRaw(p, 0xE0 | ch, 0x00, 0x40); // pitch bend center
                SendRaw(p, 0xB0 | ch, 1, 0);       // modulation
                SendRaw(p, 0xD0 | ch, 0, 0);       // channel aftertouch
                SendRaw(p, 0xB0 | ch, 64, 0);      // sustain off
                SendRaw(p, 0xB0 | ch, 123, 0);     // all notes off
            }
        }
    });
}
