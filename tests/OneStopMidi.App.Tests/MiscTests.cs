using OneStopMidi.App.Engine;
using OneStopMidi.App.ViewModels;
using OneStopMidi.Core;
using Xunit;

namespace OneStopMidi.App.Tests;

public class KaraokeLayoutTests
{
    private static LyricSyllable S(long tick, string text, bool line = false, bool para = false) => new(tick, text, line, para);

    [Fact]
    public void Lines_And_Pages()
    {
        var lyrics = new List<LyricSyllable>();
        long t = 0;
        // 10 lines of two syllables, paragraph at line 4
        for (var i = 0; i < 10; i++)
        {
            lyrics.Add(S(t += 10, $"L{i}a ", line: i > 0, para: i == 4));
            lyrics.Add(S(t += 10, $"L{i}b"));
        }
        var layout = new KaraokeLayout(lyrics);
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(4, layout.Pages[0].Count);   // paragraph break forces a new page
        Assert.Equal(6, layout.Pages[1].Count);
        Assert.Equal("L0a L0b", layout.Pages[0][0].Text);
        Assert.Equal(0, layout.PageIndexAt(0));
        Assert.Equal(0, layout.PageIndexAt(layout.Pages[1][0].StartTick - 1));
        Assert.Equal(1, layout.PageIndexAt(layout.Pages[1][0].StartTick));
    }

    [Fact]
    public void LongText_SplitsIntoPagesOfSevenLines()
    {
        var lyrics = Enumerable.Range(0, 20).Select(i => S(i * 10, $"w{i}", line: i > 0)).ToList();
        var layout = new KaraokeLayout(lyrics);
        Assert.Equal([7, 7, 6], layout.Pages.Select(p => p.Count));
        Assert.True(new KaraokeLayout([]).IsEmpty);
    }
}

public class NullEngineTests
{
    [Fact]
    public void Load_RejectsMissingAndNonMidiFiles()
    {
        using var dir = new TempDir();
        using var engine = new NullPlayerEngine();
        Assert.Throws<FileNotFoundException>(() => engine.Load(dir.File("nope.mid")));
        var bogus = dir.File("bogus.mid");
        File.WriteAllText(bogus, "this is not midi data at all");
        Assert.Throws<InvalidDataException>(() => engine.Load(bogus));
        Assert.Null(engine.Song);
    }

    [Fact]
    public void Load_ValidHeader_SetsUpSong_AndSoloLogicWorks()
    {
        using var dir = new TempDir();
        var path = dir.File("ok.mid");
        File.WriteAllBytes(path, [0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 1, 0, 2, 0x01, 0xE0]);
        using var engine = new NullPlayerEngine();
        var songChanged = 0;
        engine.SongChanged += (_, _) => songChanged++;
        engine.Load(path);
        Assert.NotNull(engine.Song);
        Assert.Equal(480, engine.Song!.Ppq);
        Assert.Equal(1, engine.Song.Format);
        Assert.Equal(1, songChanged);
        Assert.Equal(new MusicalPosition(1, 1, 0), engine.Position);

        engine.SetSolo(2, true, additive: false);
        engine.SetSolo(5, true, additive: true);
        Assert.Equal([2, 5], engine.Channels.Where(c => c.Soloed).Select(c => c.Channel));
        engine.SetSolo(7, true, additive: false);
        Assert.Equal([7], engine.Channels.Where(c => c.Soloed).Select(c => c.Channel));

        engine.SeekBars(3);
        Assert.Equal(new MusicalPosition(4, 1, 0), engine.Position);
    }

    [Fact]
    public void EngineFactory_AlwaysReturnsAnEngine()
    {
        using var engine = EngineFactory.Create();
        Assert.NotNull(engine);
        if (EngineFactory.IsSimulated(engine)) Assert.NotNull(EngineFactory.FallbackReason);
    }

    [Fact]
    public void GeneralMidi_Tables()
    {
        Assert.Equal(128, GeneralMidi.Programs.Length);
        Assert.Equal("Acoustic Grand Piano", GeneralMidi.ProgramName(0));
        Assert.Equal("Gunshot", GeneralMidi.ProgramName(127));
        Assert.Equal("Guitar", GeneralMidi.FamilyOf(25));
        Assert.Equal("Standard Kit", GeneralMidi.DrumKitName(0));
    }
}
