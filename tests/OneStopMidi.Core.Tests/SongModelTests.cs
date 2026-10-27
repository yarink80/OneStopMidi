using System.Text;
using OneStopMidi.Core.Midi;
using Xunit;

namespace OneStopMidi.Core.Tests;

public class SongModelTests
{
    [Fact]
    public void TempoMap_ConvertsTicksAndTime()
    {
        var b = new SongBuilder(ppq: 480);
        b.Track().Tempo(0, 120).Tempo(960, 60).Note(0, 60, 0, 10).End(1920);
        var song = b.Song();

        Assert.Equal(TimeSpan.FromSeconds(1), song.TickToTime(960));      // 2 quarters at 120
        Assert.Equal(TimeSpan.FromSeconds(2), song.TickToTime(1440));     // + 1 quarter at 60
        Assert.Equal(TimeSpan.FromSeconds(3), song.Duration);
        Assert.Equal(1440, song.TempoMap.MicrosecondsToTick(2_000_000), 6);
        Assert.Equal(120, song.TempoMap.BpmAt(959), 6);
        Assert.Equal(60, song.TempoMap.BpmAt(960), 6);
        Assert.Equal(2, song.TempoEventCount);
        Assert.Equal(2, song.Tempos.Count);
    }

    [Fact]
    public void TempoMap_DefaultsTo120()
    {
        var map = new TempoMap(96, Array.Empty<(long, int)>());
        Assert.Equal(TimeSpan.FromSeconds(0.5), map.TickToTime(96));
    }

    [Fact]
    public void Musical_FourFour()
    {
        var map = new TimeSignatureMap(480, Array.Empty<TimeSignatureChange>());
        Assert.Equal(new MusicalPosition(1, 1, 0), map.ToMusical(0));
        Assert.Equal(new MusicalPosition(1, 2, 10), map.ToMusical(490));
        Assert.Equal(new MusicalPosition(2, 1, 0), map.ToMusical(1920));
        Assert.Equal(new MusicalPosition(3, 4, 479), map.ToMusical(1920 * 3 - 1));
        Assert.Equal(1920 * 2 + 480 * 3 + 7, map.FromMusical(new MusicalPosition(3, 4, 7)));
        Assert.Equal("2 1 0", map.ToMusical(1920).ToString());
    }

    [Fact]
    public void Musical_ThreeFour_And_SevenEight()
    {
        var three = new TimeSignatureMap(480, new[] { new TimeSignatureChange(0, 3, 4) });
        Assert.Equal(new MusicalPosition(2, 1, 0), three.ToMusical(1440));
        Assert.Equal(new MusicalPosition(1, 3, 479), three.ToMusical(1439));
        Assert.Equal(1440 + 480, three.FromMusical(new MusicalPosition(2, 2, 0)));

        var seven = new TimeSignatureMap(480, new[] { new TimeSignatureChange(0, 7, 8) });
        // beat = eighth = 240 ticks, bar = 1680 ticks
        Assert.Equal(new MusicalPosition(1, 7, 0), seven.ToMusical(1440));
        Assert.Equal(new MusicalPosition(2, 1, 0), seven.ToMusical(1680));
        Assert.Equal(new MusicalPosition(2, 2, 100), seven.ToMusical(1680 + 240 + 100));
        Assert.Equal(1680 * 4 + 240 * 6 + 5, seven.FromMusical(new MusicalPosition(5, 7, 5)));
        Assert.Equal(240, seven.BeatTicksAt(0));
    }

    [Fact]
    public void Musical_MidSongChange()
    {
        // 2 bars of 4/4 (3840 ticks), then 3/4, then 7/8 after two 3/4 bars.
        var song = new SongBuilder(480).Track().TimeSig(0, 4, 4).TimeSig(3840, 3, 4).TimeSig(3840 + 2880, 7, 8)
            .Note(0, 60, 0, 10).End(20000).Done().Song();
        Assert.Equal(3, song.TimeSignatures.Count);
        Assert.Equal(new MusicalPosition(2, 4, 0), song.ToMusical(3840 - 480));
        Assert.Equal(new MusicalPosition(3, 1, 0), song.ToMusical(3840));
        Assert.Equal(new MusicalPosition(4, 3, 0), song.ToMusical(3840 + 1440 + 960));
        Assert.Equal(new MusicalPosition(5, 1, 0), song.ToMusical(6720));
        Assert.Equal(new MusicalPosition(5, 2, 0), song.ToMusical(6720 + 240));
        Assert.Equal(new MusicalPosition(6, 1, 0), song.ToMusical(6720 + 1680));

        foreach (long t in new long[] { 0, 1, 479, 3839, 3840, 5000, 6719, 6720, 7000, 9999 })
            Assert.Equal(t, song.FromMusical(song.ToMusical(t)));
    }

    [Fact]
    public void Musical_ChangeOffBarLine_StartsNewBar()
    {
        var map = new TimeSignatureMap(480, new[] { new TimeSignatureChange(0, 4, 4), new TimeSignatureChange(2400, 3, 4) });
        Assert.Equal(new MusicalPosition(3, 1, 0), map.ToMusical(2400));
    }

    [Fact]
    public void SequenceName_TrackNames_And_Warnings()
    {
        var b = new SongBuilder();
        b.Track().Name("Title").Tempo(0, 120);
        b.Track().Name("Bass").Program(0, 1, 33).CC(0, 1, 7, 100).Note(1, 40, 0, 100);
        b.Track().Name("Lead").Note(2, 70, 10, 100).Program(20, 2, 80);
        b.Track().Name("Kit").CC(0, 9, 7, 110).Note(9, 36, 0, 10);
        var song = b.Song();

        Assert.Equal("Title", song.SequenceName);
        Assert.Equal("Bass", song.ChannelTrackNames[1]);
        Assert.Equal("Lead", song.ChannelTrackNames[2]);
        Assert.Equal("Kit", song.ChannelTrackNames[9]);
        Assert.Null(song.ChannelTrackNames[0]);
        Assert.True(song.ChannelHasNotes[1]);
        Assert.False(song.ChannelHasNotes[0]);

        Assert.Equal(2, song.Warnings.Count);
        Assert.Contains(song.Warnings, w => w.StartsWith("Channel 3:") && w.Contains("program"));
        Assert.Contains(song.Warnings, w => w.StartsWith("Channel 3:") && w.Contains("volume"));
    }

    [Fact]
    public void Lyrics_FromLyricEvents_WithLineBreaks()
    {
        var b = new SongBuilder();
        b.Track().Name("Song").Text(0, "just a comment")
            .Lyric(0, "Hel").Lyric(100, "lo ").Lyric(200, "world\r").Lyric(300, "Sec").Lyric(400, "ond\n").Lyric(500, "\\New para");
        var lyrics = b.Song().Lyrics;

        Assert.Equal(new[] { "Hel", "lo ", "world", "Sec", "ond", "New para" }, lyrics.Select(l => l.Text));
        Assert.False(lyrics[1].StartsNewLine);
        Assert.True(lyrics[3].StartsNewLine);
        Assert.False(lyrics[3].StartsNewParagraph);
        Assert.True(lyrics[5].StartsNewParagraph);
        Assert.True(lyrics[5].StartsNewLine);
        Assert.Equal(300, lyrics[3].Tick);
    }

    [Fact]
    public void Lyrics_KaraokeTextEvents()
    {
        var b = new SongBuilder();
        b.Track().Name("Song").Text(0, "(c) someone");
        b.Track().Name("Words").Text(0, "@KMIDI KARAOKE FILE").Text(0, "@V0100").Text(0, "@TMy Title")
            .Text(100, "\\Twin").Text(150, "kle ").Text(200, "twin").Text(250, "kle")
            .Text(300, "/Lit").Text(350, "tle ").Text(400, "star");
        var lyrics = b.Song().Lyrics;

        Assert.Equal(new[] { "Twin", "kle ", "twin", "kle", "Lit", "tle ", "star" }, lyrics.Select(l => l.Text));
        Assert.True(lyrics[0].StartsNewParagraph);
        Assert.True(lyrics[4].StartsNewLine);
        Assert.False(lyrics[4].StartsNewParagraph);
        Assert.DoesNotContain(lyrics, l => l.Text.Contains('@') || l.Text.Contains("(c)"));
    }

    [Fact]
    public void Lyrics_TextEncodingIsApplied()
    {
        var b = new SongBuilder();
        b.Track().Add(MidiEvent.Meta(0, MidiEvent.MetaLyric, new byte[] { 0xE9, (byte)'t', 0xE9 })); // "été" in 1252
        Assert.Equal("été", b.Song().Lyrics[0].Text);

        var cyr = Encoding.GetEncoding(1251);
        b = new SongBuilder();
        b.Track().Add(MidiEvent.Meta(0, MidiEvent.MetaLyric, cyr.GetBytes("Привет")));
        Assert.Equal("Привет", b.Song(new SongLoadOptions { TextEncoding = cyr }).Lyrics[0].Text);
    }

    [Theory]
    [InlineData("%Gb7", "Gb7")]
    [InlineData("[F7]", "F7")]
    [InlineData("(G13)", "G13")]
    [InlineData("Cm", "Cm")]
    [InlineData("Gb13b9", "Gb13b9")]
    [InlineData("C#m7/G#", "C#m7/G#")]
    [InlineData("Bbmaj7", "Bbmaj7")]
    public void ChordParser_RecognisesChords(string text, string expected)
    {
        Assert.True(ChordParser.TryParseEvent(text, allowPlain: true, out var chord));
        Assert.Equal(expected, chord);
    }

    [Theory]
    [InlineData("Hello")]
    [InlineData("A")]
    [InlineData("(oh yeah)")]
    [InlineData("Bad")]
    [InlineData("Add")]
    [InlineData("")]
    public void ChordParser_RejectsWords(string text)
    {
        Assert.False(ChordParser.TryParseEvent(text, allowPlain: true, out _));
    }

    [Fact]
    public void Chords_EmbeddedAreExtracted_AndRemovedFromLyrics()
    {
        var b = new SongBuilder();
        b.Track().Name("Song")
            .Text(0, "%Gb7").Text(480, "[F7]").Text(960, "(G13)").Text(1440, "Cm")
            .Lyric(0, "[Am]Hel").Lyric(100, "lo")
            .Add(MidiEvent.Meta(1920, 0x7F, new byte[] { 0x43, 0x7B, 0x01, 0x33, 0x08, 0x7F, 0x00 })) // E minor
            .Note(0, 60, 0, 2000);
        var song = b.Song(new SongLoadOptions { ChordMode = ChordDisplayMode.Embedded });

        Assert.Equal(new[] { "Gb7", "Am", "F7", "G13", "Cm", "Em" }, song.Chords.Select(c => c.Name));
        Assert.All(song.Chords, c => Assert.False(c.Detected));
        Assert.Equal(new[] { "Hel", "lo" }, song.Lyrics.Select(l => l.Text));
    }

    [Fact]
    public void Chords_AutoDetect()
    {
        var b = new SongBuilder(480);
        b.Track().Name("x")
            .Note(0, 48, 0, 1920).Note(0, 64, 0, 1920).Note(0, 67, 0, 1920)           // C major (bar 1)
            .Note(0, 57, 1920, 1920).Note(0, 60, 1920, 1920).Note(0, 64, 1920, 1920)  // A minor (bar 2)
            .Note(0, 43, 3840, 1920).Note(0, 59, 3840, 1920).Note(0, 62, 3840, 1920).Note(0, 65, 3840, 1920) // G7
            .Note(9, 37, 0, 5000).Note(9, 42, 0, 5000); // drums ignored
        var song = b.Song(new SongLoadOptions { ChordMode = ChordDisplayMode.AutoDetect });

        Assert.Equal(new[] { "C", "Am", "G7" }, song.Chords.Select(c => c.Name));
        Assert.Equal(new long[] { 0, 1920, 3840 }, song.Chords.Select(c => c.Tick));
        Assert.All(song.Chords, c => Assert.True(c.Detected));

        // Prefer-embedded falls back to detection when the file has no chords; Off shows nothing.
        Assert.Equal(3, b.Song(new SongLoadOptions { ChordMode = ChordDisplayMode.PreferEmbeddedThenDetect }).Chords.Count);
        Assert.Empty(b.Song(new SongLoadOptions { ChordMode = ChordDisplayMode.Off }).Chords);
    }
}
