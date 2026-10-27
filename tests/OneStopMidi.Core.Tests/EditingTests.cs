using OneStopMidi.Core.Midi;
using Xunit;

namespace OneStopMidi.Core.Tests;

public class EditingTests
{
    /// <summary>Three bars (4/4, 480 PPQ), one note per bar: 60, 62, 64.</summary>
    private static MidiSong ThreeBars()
    {
        var b = new SongBuilder(480);
        b.Track().Name("Seq").Tempo(0, 120);
        b.Track().Name("Piano").Note(0, 60, 0, 480).Note(0, 62, 1920, 480).Note(0, 64, 3840, 480).End(5760);
        return b.Song();
    }

    private static (long Tick, int Note)[] NoteOns(IMidiSong song) =>
        ((MidiSong)song).Events.Where(e => e.IsNoteOn).Select(e => (e.Tick, (int)e.Data1)).ToArray();

    [Fact]
    public void Cut_Paste_Undo_Redo()
    {
        using var h = new EngineHarness(ThreeBars());
        var e = h.Engine;
        Assert.False(e.CanUndo);
        Assert.False(e.CanPaste);

        e.LeftLocator = 1920;
        e.RightLocator = 3840;
        e.CutSelection();
        Assert.Equal(new[] { (0L, 60), (1920L, 64) }, NoteOns(e.Song!));
        Assert.Equal(3840, e.Song!.LengthTicks);
        Assert.True(e.Song.IsModified);
        Assert.True(e.CanUndo);
        Assert.True(e.CanPaste);

        e.Seek(1920);
        e.Paste();
        Assert.Equal(new[] { (0L, 60), (1920L, 62), (3840L, 64) }, NoteOns(e.Song));
        Assert.Equal(5760, e.Song.LengthTicks);

        e.Undo();
        Assert.Equal(new[] { (0L, 60), (1920L, 64) }, NoteOns(e.Song));
        e.Undo();
        Assert.Equal(new[] { (0L, 60), (1920L, 62), (3840L, 64) }, NoteOns(e.Song));
        Assert.False(e.Song.IsModified);
        Assert.False(e.CanUndo);
        Assert.True(e.CanRedo);

        e.Redo();
        Assert.Equal(new[] { (0L, 60), (1920L, 64) }, NoteOns(e.Song));
        Assert.True(e.Song.IsModified);
        e.Redo();
        Assert.Equal(3, NoteOns(e.Song).Length);
        Assert.False(e.CanRedo);
    }

    [Fact]
    public void Copy_Paste_DuplicatesRange()
    {
        using var h = new EngineHarness(ThreeBars());
        var e = h.Engine;
        e.LeftLocator = 0;
        e.RightLocator = 1920;
        e.CopySelection();
        Assert.False(e.CanUndo); // copy does not change the song
        e.Seek(5760);
        e.Paste();
        Assert.Equal(new[] { (0L, 60), (1920L, 62), (3840L, 64), (5760L, 60) }, NoteOns(e.Song!));
        Assert.Equal(7680, e.Song!.LengthTicks);
        // Every note-on still has a matching note-off.
        var song = (MidiSong)e.Song;
        Assert.Equal(song.Events.Count(x => x.IsNoteOn), song.Events.Count(x => x.IsNoteOff));
    }

    [Fact]
    public void Delete_ClosesNotesCutInHalf()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 1800, 200).Note(0, 62, 2000, 2000).Note(0, 64, 4000, 100).End(5760);
        using var h = new EngineHarness(b.Song());
        var e = h.Engine;
        e.LeftLocator = 1920;
        e.RightLocator = 3840;
        e.DeleteSelection();
        var song = (MidiSong)e.Song!;
        // Note 60 started before the range: its note-off moves to the range start.
        Assert.Contains(song.Events, x => x.IsNoteOff && x.Data1 == 60 && x.Tick == 1920);
        // Note 62 started inside the range: removed with its note-off (which was after the range).
        Assert.DoesNotContain(song.Events, x => x.Data1 == 62 && x.IsChannelMessage);
        Assert.Contains(song.Events, x => x.IsNoteOn && x.Data1 == 64 && x.Tick == 4000 - 1920);
        Assert.Equal(3840, song.LengthTicks);
        Assert.False(e.CanPaste); // delete does not fill the clipboard
    }

    [Fact]
    public void Copy_AddsNoteOffsForNotesCrossingRangeEnd()
    {
        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 100, 3000).End(5760);
        var song = b.Song();
        var clip = song.CopyRange(0, 1920);
        var events = clip.Tracks.SelectMany(t => t).ToList();
        Assert.Contains(events, x => x.IsNoteOn && x.Tick == 100);
        Assert.Contains(events, x => x.IsNoteOff && x.Tick == 1920);
    }

    [Fact]
    public void EraseMutedChannels_RemovesChannelEvents()
    {
        var b = new SongBuilder(480);
        b.Track().Program(0, 1, 5).Note(0, 60, 0, 100).Note(1, 40, 0, 100).End(1920);
        using var h = new EngineHarness(b.Song());
        var e = h.Engine;
        e.EraseMutedChannels(); // nothing muted: no-op
        Assert.False(e.CanUndo);
        e.SetMute(1, true);
        e.EraseMutedChannels();
        var song = (MidiSong)e.Song!;
        Assert.DoesNotContain(song.Events, x => x.IsChannelMessage && x.Channel == 1);
        Assert.Contains(song.Events, x => x.IsChannelMessage && x.Channel == 0);
        Assert.False(e.Channels[1].HasNotes);
        e.Undo();
        Assert.True(e.Channels[1].HasNotes);
    }

    [Fact]
    public void SetSequenceName_IsUndoable()
    {
        using var h = new EngineHarness(ThreeBars());
        int songChanged = 0;
        h.Engine.SongChanged += (_, _) => songChanged++;
        h.Engine.SetSequenceName("New Name");
        Assert.Equal("New Name", h.Engine.Song!.SequenceName);
        Assert.Equal(1, songChanged);
        h.Engine.Undo();
        Assert.Equal("Seq", h.Engine.Song.SequenceName);

        var b = new SongBuilder(480);
        b.Track().Note(0, 60, 0, 10);
        using var h2 = new EngineHarness(b.Song());
        Assert.Null(h2.Engine.Song!.SequenceName);
        h2.Engine.SetSequenceName("Added");
        Assert.Equal("Added", h2.Engine.Song.SequenceName);
    }

    [Fact]
    public void Save_Revert_And_LocatorPersistence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"onestopmidi-{Guid.NewGuid():N}.mid");
        File.WriteAllBytes(path, new SongBuilder(480).Track().Name("Orig").Note(0, 60, 0, 480).End(7680).Done().Build());
        try
        {
            using var h = new EngineHarness();
            var e = h.Engine;
            e.Load(path);
            Assert.Equal(7680, e.RightLocator);
            e.SetSequenceName("Saved");
            e.LeftLocator = 1920;
            e.RightLocator = 3840;
            Assert.True(e.Song!.IsModified);
            e.Save(path);
            Assert.False(e.Song.IsModified);

            e.SetSequenceName("Unsaved");
            e.RevertToSaved();
            Assert.Equal("Saved", e.Song!.SequenceName);
            Assert.False(e.Song.IsModified);
            Assert.False(e.CanUndo);
            Assert.Equal(1920, e.LeftLocator);
            Assert.Equal(3840, e.RightLocator);

            e.Save(path, format: 0);
            Assert.Equal(0, e.Song!.Format);
            Assert.Equal(0, SmfReader.ReadFile(path).Format);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EditWhilePlaying_KeepsPlaying()
    {
        using var h = new EngineHarness(ThreeBars());
        h.Engine.Play();
        h.Advance(0.5);
        h.Engine.LeftLocator = 1920;
        h.Engine.RightLocator = 3840;
        h.Engine.DeleteSelection();
        Assert.Equal(PlaybackState.Playing, h.Engine.State);
        h.Advance(2.0); // note 64 is now at 1920 (2.0 s)
        Assert.Contains(h.Port.NoteOns, n => n.Note == 64);
        Assert.DoesNotContain(h.Port.NoteOns, n => n.Note == 62);
    }
}
