using OneStopMidi.Core.Midi;
using OneStopMidi.Core.Output;
using Xunit;

namespace OneStopMidi.Core.Tests;

/// <summary>Engine tests at 480 PPQ / 120 BPM: 960 ticks per second.</summary>
public class PlayerEngineTests
{
    private static MidiSong TwoChannelSong()
    {
        var b = new SongBuilder(480);
        b.Track().Name("T").Tempo(0, 120).End(1920);
        b.Track().Name("Piano").Note(0, 60, 0, 960);
        b.Track().Name("Drums").Note(9, 36, 0, 960);
        return b.Song();
    }

    [Fact]
    public void Transpose_SkipsDrumChannel_AndNoteOffMatchesNoteOn()
    {
        using var h = new EngineHarness(TwoChannelSong());
        h.Engine.Transpose = 2;
        h.Engine.Play();
        h.Advance(0.1);
        Assert.Contains((0, 62, 100), h.Port.NoteOns);
        Assert.Contains((9, 36, 100), h.Port.NoteOns);

        h.Engine.Transpose = 5; // change mid-note
        h.Advance(1.0);
        Assert.Contains((0, 62), h.Port.NoteOffs);
        Assert.DoesNotContain((0, 65), h.Port.NoteOffs);
        Assert.Contains((9, 36), h.Port.NoteOffs);
    }

    [Fact]
    public void Transpose_RespectsCustomDrumChannels()
    {
        using var h = new EngineHarness(TwoChannelSong());
        h.Engine.DrumChannels[0] = true;
        h.Engine.Transpose = -3;
        h.Engine.Play();
        h.Advance(0.05);
        Assert.Contains((0, 60, 100), h.Port.NoteOns);
    }

    [Fact]
    public void Mute_SendsNoteOffForSoundingNotes_AndSuppressesNewNotes()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 900).Note(0, 62, 960, 100).Note(1, 50, 960, 100).End(1920);
        using var h = new EngineHarness(b.Song());
        int changed = -2;
        h.Engine.ChannelsChanged += (_, ch) => changed = ch;
        h.Engine.Play();
        h.Advance(0.1);
        h.Port.Clear();

        h.Engine.SetMute(0, true);
        Assert.Contains((0, 60), h.Port.NoteOffs);
        Assert.Equal(0, changed);
        Assert.True(h.Engine.Channels[0].Muted);

        h.Advance(1.0);
        Assert.DoesNotContain(h.Port.NoteOns, n => n.Channel == 0);
        Assert.Contains(h.Port.NoteOns, n => n.Channel == 1);
    }

    [Fact]
    public void Solo_SilencesOtherChannels()
    {
        using var h = new EngineHarness(TwoChannelSong());
        h.Engine.Play();
        h.Advance(0.1);
        h.Port.Clear();
        h.Engine.SetSolo(9, true, additive: false);
        Assert.Contains((0, 60), h.Port.NoteOffs);
        Assert.DoesNotContain((9, 36), h.Port.NoteOffs);
        h.Engine.SetSolo(0, true, additive: true);
        Assert.True(h.Engine.Channels[0].Soloed && h.Engine.Channels[9].Soloed);
        h.Engine.SetSolo(1, true, additive: false);
        Assert.False(h.Engine.Channels[0].Soloed || h.Engine.Channels[9].Soloed);
    }

    [Fact]
    public void Loop_WrapsToLeftLocator_WithoutSongEnded()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 240).Note(0, 62, 960, 240).Note(0, 64, 1920, 240).End(3840);
        using var h = new EngineHarness(b.Song());
        h.Engine.LeftLocator = 960;
        h.Engine.RightLocator = 1920;
        h.Engine.LoopEnabled = true;
        h.Engine.Play();
        Assert.Equal(960, h.Engine.PositionTicks);

        h.Advance(1.05); // past the right locator once
        Assert.Equal(2, h.Port.NoteOns.Count(n => n.Note == 62));
        Assert.DoesNotContain(h.Port.NoteOns, n => n.Note is 60 or 64);
        long pos = h.Engine.PositionTicks;
        Assert.InRange(pos, 960, 1920);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);

        h.Advance(5);
        Assert.Equal(0, h.SongEndedCount);
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
    }

    [Fact]
    public void Loop_WithoutLocatorRange_LoopsWholeSong()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 240).End(960);
        using var h = new EngineHarness(b.Song());
        h.Engine.RightLocator = 0;
        h.Engine.LoopEnabled = true;
        h.Engine.Play();
        h.Advance(2.5);
        Assert.Equal(3, h.Port.NoteOns.Count(n => n.Note == 60));
        Assert.Equal(0, h.SongEndedCount);
    }

    private static MidiSong ChaseSong()
    {
        var b = new SongBuilder(480);
        b.Track().Program(0, 0, 5).CC(0, 0, 7, 80).CC(10, 0, 10, 30).CC(0, 0, 0, 1)
            .Note(0, 60, 0, 100).Note(0, 62, 1920, 100)
            .Add(MidiEvent.ChannelMessage(500, 0xE0, 0, 0x50))
            .End(3840);
        return b.Song();
    }

    [Fact]
    public void Chase_SendsLatestControllersOnSeekAndContinue()
    {
        using var h = new EngineHarness(ChaseSong());
        h.Engine.Seek(960);
        Assert.Empty(h.Port.Messages); // stopped: chase happens on continue
        h.Engine.PauseContinue();

        Assert.Contains((0, 5), h.Port.ProgramChanges);
        Assert.Contains((0, 7, 80), h.Port.ControlChanges);
        Assert.Contains((0, 10, 30), h.Port.ControlChanges);
        Assert.Contains((0, 0, 1), h.Port.ControlChanges);
        Assert.Contains(h.Port.Messages, m => m[0] == 0xE0 && m[1] == 0 && m[2] == 0x50);
        var ch0 = h.Engine.Channels[0];
        Assert.Equal((5, 80, 30, 1), (ch0.Program, ch0.Volume, ch0.Pan, ch0.BankMsb));
        Assert.Equal("Electric Piano 2", ch0.InstrumentName);

        // Seek while playing chases again.
        h.Port.Clear();
        h.Engine.Seek(1500);
        Assert.Contains((0, 5), h.Port.ProgramChanges);
        Assert.Contains((0, 123, 0), h.Port.ControlChanges); // all notes off on seek
    }

    [Fact]
    public void Chase_Disabled_SendsNothing()
    {
        using var h = new EngineHarness(ChaseSong());
        h.Engine.ChaseEvents = false;
        h.Engine.Seek(960);
        h.Engine.PauseContinue();
        Assert.Empty(h.Port.ProgramChanges);
    }

    [Fact]
    public void Mixer_ReflectsFileEventsAtLoad()
    {
        using var h = new EngineHarness(ChaseSong());
        var ch0 = h.Engine.Channels[0];
        Assert.Equal(5, ch0.Program);
        Assert.Equal(80, ch0.Volume);
        Assert.Equal(PlayerEngine.DefaultPan, ch0.Pan); // pan event is after the first note
        Assert.True(ch0.HasNotes);
        Assert.Equal("Standard", h.Engine.Channels[9].InstrumentName);
        Assert.True(h.Engine.Channels[9].IsDrumChannel);
    }

    [Fact]
    public void SongEnded_RaisedOnlyAtNaturalEnd()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 240).End(480);
        using var h = new EngineHarness(b.Song());
        var states = new List<PlaybackState>();
        h.Engine.StateChanged += (_, _) => states.Add(h.Engine.State);

        h.Engine.Play();
        h.Advance(0.2);
        h.Engine.Stop();
        Assert.Equal(0, h.SongEndedCount);

        h.Engine.Play();
        h.Advance(0.6);
        Assert.Equal(1, h.SongEndedCount);
        Assert.Equal(PlaybackState.Stopped, h.Engine.State);
        Assert.Equal(new[] { PlaybackState.Playing, PlaybackState.Stopped, PlaybackState.Playing, PlaybackState.Stopped }, states);
    }

    [Fact]
    public void Stop_Twice_ResetsPosition_PauseKeepsIt()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 240).End(3840);
        using var h = new EngineHarness(b.Song());
        h.Engine.Play();
        h.Advance(0.5);
        h.Engine.PauseContinue();
        Assert.Equal(PlaybackState.Paused, h.Engine.State);
        Assert.Equal(480, h.Engine.PositionTicks);
        h.Advance(1);
        Assert.Equal(480, h.Engine.PositionTicks);
        h.Engine.PauseContinue();
        h.Advance(0.5);
        Assert.Equal(960, h.Engine.PositionTicks);
        h.Engine.Stop();
        Assert.Equal(960, h.Engine.PositionTicks);
        Assert.Contains((0, 64, 0), h.Port.ControlChanges); // sustain off
        h.Engine.Stop();
        Assert.Equal(0, h.Engine.PositionTicks);
    }

    [Fact]
    public void TempoPercent_And_BaseTempoOverride()
    {
        var b = new SongBuilder(480);
        b.Track().Tempo(0, 120).Note(0, 60, 0, 240).End(9600);
        using var h = new EngineHarness(b.Song());
        Assert.True(h.Engine.CanSetBaseTempo);
        h.Engine.TempoPercent = 100;
        h.Engine.Play();
        h.Advance(0.5);
        Assert.InRange(h.Engine.PositionTicks, 955, 965);
        Assert.Equal(240, h.Engine.EffectiveBpm, 3);
        Assert.Equal(120, h.Engine.CurrentBpm, 3);

        h.Engine.TempoPercent = 0;
        h.Engine.BaseTempoOverride = 60;
        long before = h.Engine.PositionTicks;
        h.Advance(1.0);
        Assert.InRange(h.Engine.PositionTicks - before, 475, 485);
        Assert.Equal(60, h.Engine.CurrentBpm, 3);
        h.Engine.BaseTempoOverride = null;
        Assert.Equal(120, h.Engine.CurrentBpm, 3);
    }

    [Fact]
    public void BaseTempoOverride_IgnoredWithSeveralTempos()
    {
        var b = new SongBuilder(480);
        b.Track().Tempo(0, 120).Tempo(960, 90).Note(0, 60, 0, 240).End(1920);
        using var h = new EngineHarness(b.Song());
        Assert.False(h.Engine.CanSetBaseTempo);
        h.Engine.BaseTempoOverride = 200;
        Assert.Null(h.Engine.BaseTempoOverride);
    }

    [Fact]
    public void SeekBars_MovesByWholeBars()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 240).End(1920 * 8);
        using var h = new EngineHarness(b.Song());
        h.Engine.SeekBars(3);
        Assert.Equal(new MusicalPosition(4, 1, 0), h.Engine.Position);
        h.Engine.SeekBars(-10);
        Assert.Equal(0, h.Engine.PositionTicks);
        h.Engine.SeekBars(100);
        Assert.Equal(1920 * 8, h.Engine.PositionTicks);
    }

    [Fact]
    public void Routing_ToSecondPortAndOtherChannel()
    {
        using var h = new EngineHarness(TwoChannelSong());
        h.Engine.SelectedOutputDevices = new[] { 0, 1 };
        h.Engine.Routes[0] = new ChannelRoute(1, 5);
        h.Engine.Play();
        h.Advance(0.05);
        var portB = h.Provider.OpenedPorts[1];
        Assert.Contains((5, 60, 100), portB.NoteOns);
        Assert.DoesNotContain(h.Provider.OpenedPorts[0].NoteOns, n => n.Note == 60);
        Assert.Contains((9, 36, 100), h.Provider.OpenedPorts[0].NoteOns);
    }

    [Fact]
    public void Click_PlaysNote33OnChannel10EachBeat()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 240).End(3840);
        using var h = new EngineHarness(b.Song());
        h.Engine.ClickEnabled = true;
        h.Engine.Play();
        h.Advance(1.1); // beats at 0, 480, 960
        Assert.Equal(3, h.Port.NoteOns.Count(n => n.Channel == 9 && n.Note == 33));
    }

    [Fact]
    public void FullVolume_SetAllVolumes_ResetDevices()
    {
        using var h = new EngineHarness(TwoChannelSong());
        h.Engine.FullVolume();
        Assert.All(h.Engine.Channels, c => Assert.Equal(127, c.Volume));
        Assert.Equal(16, h.Port.ControlChanges.Count(c => c.Controller == 7 && c.Value == 127));

        h.Engine.SetVolume(3, 100);
        h.Engine.SetAllVolumes(110, relative: true);
        Assert.Equal(110, h.Engine.Channels[0].Volume);
        Assert.Equal(83, h.Engine.Channels[3].Volume);
        h.Engine.SetAllVolumes(50, relative: false);
        Assert.All(h.Engine.Channels, c => Assert.Equal(50, c.Volume));

        h.Port.Clear();
        h.Engine.ResetDevices();
        Assert.Equal(16, h.Port.Messages.Count(m => (m[0] & 0xF0) == 0xE0 && m[1] == 0 && m[2] == 0x40));
        Assert.Equal(16, h.Port.ControlChanges.Count(c => c.Controller == 64 && c.Value == 0));
        Assert.Equal(16, h.Port.ControlChanges.Count(c => c.Controller == 1 && c.Value == 0));
        Assert.Equal(16, h.Port.Messages.Count(m => (m[0] & 0xF0) == 0xD0));
    }

    [Fact]
    public void MixerSetters_SendControllersAndProgram()
    {
        using var h = new EngineHarness(TwoChannelSong());
        h.Engine.SetPan(2, 10);
        h.Engine.SetReverb(2, 20);
        h.Engine.SetChorus(2, 30);
        h.Engine.SetProgram(2, 40, 1, 2);
        Assert.Contains((2, 10, 10), h.Port.ControlChanges);
        Assert.Contains((2, 91, 20), h.Port.ControlChanges);
        Assert.Contains((2, 93, 30), h.Port.ControlChanges);
        Assert.Contains((2, 0, 1), h.Port.ControlChanges);
        Assert.Contains((2, 32, 2), h.Port.ControlChanges);
        Assert.Contains((2, 40), h.Port.ProgramChanges);
        Assert.Equal("Violin", h.Engine.Channels[2].InstrumentName);
    }

    [Fact]
    public void SysEx_IsSentWithF0Prefix()
    {
        var b = new SongBuilder(480);
        b.Track().Add(MidiEvent.SysEx(0, 0xF0, new byte[] { 0x7E, 0x7F, 0x09, 0x01, 0xF7 })).Note(0, 60, 0, 10).End(480);
        using var h = new EngineHarness(b.Song());
        h.Engine.Play();
        h.Advance(0.01);
        Assert.Contains(h.Port.Messages, m => m.SequenceEqual(new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 }));
    }

    [Fact]
    public void NoSong_OperationsAreSafe()
    {
        using var h = new EngineHarness();
        h.Engine.Play();
        h.Engine.PauseContinue();
        h.Engine.Seek(100);
        h.Engine.SeekBars(2);
        h.Engine.CutSelection();
        h.Engine.Undo();
        h.Engine.Stop();
        Assert.Equal(PlaybackState.Stopped, h.Engine.State);
        Assert.Equal(16, h.Engine.Channels.Count);
        Assert.Equal(new MusicalPosition(1, 1, 0), h.Engine.Position);
    }

    [Fact]
    public void NoDevices_EngineStillRuns()
    {
        var engine = new PlayerEngine(new RecordingMidiOutputProvider(0), new ManualClock(), realtime: false);
        engine.Load(TwoChannelSong());
        engine.Play();
        engine.Pump();
        engine.SetVolume(0, 10);
        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.Equal(new string?[] { null }, engine.OpenPortNames);
        engine.Dispose();
    }

    [Fact]
    public void Realtime_PlaysToEnd_AndRaisesEventsOnBackgroundThread()
    {
        var b = new SongBuilder(480);
        b.Track().Tempo(0, 240).Note(0, 60, 0, 120).Note(0, 62, 240, 120).End(480); // 0.25 s
        var provider = new RecordingMidiOutputProvider(1);
        using var engine = new PlayerEngine(provider);
        using var ended = new ManualResetEventSlim();
        int positionEvents = 0;
        int callerThread = Environment.CurrentManagedThreadId, eventThread = -1;
        engine.PositionChanged += (_, _) => Interlocked.Increment(ref positionEvents);
        engine.SongEnded += (_, _) => { eventThread = Environment.CurrentManagedThreadId; ended.Set(); };
        engine.Load(b.Song());
        engine.Play();
        Assert.True(ended.Wait(TimeSpan.FromSeconds(5)), "SongEnded not raised");
        Assert.NotEqual(callerThread, eventThread);
        Assert.True(positionEvents >= 2);
        Assert.Equal(PlaybackState.Stopped, engine.State);
        var port = provider.OpenedPorts[0];
        Assert.Equal(new[] { 60, 62 }, port.NoteOns.Select(n => n.Note));
        Assert.Contains((0, 62), port.NoteOffs);
        engine.Dispose();
        Assert.True(port.IsDisposed);
    }

    [Fact]
    public void WinMm_DefaultEngine_ConstructsWithoutThrowing()
    {
        var devices = new WinMmOutputProvider().GetDevices();
        Assert.NotNull(devices);
        using var engine = new PlayerEngine();
        Assert.Equal(devices.Count, engine.GetOutputDevices().Count);
    }
}
