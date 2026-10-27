using System.Text;
using OneStopMidi.App.Playlists;
using Xunit;

namespace OneStopMidi.App.Tests;

public class PlaylistStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PlaylistStore _store;

    public PlaylistStoreTests()
    {
        _store = new PlaylistStore(Path.Combine(_dir.Path, "Playlists"));
    }

    public void Dispose() => _dir.Dispose();

    private string Song(string name) => Path.Combine(@"C:\Music", name);

    [Fact]
    public void Create_WritesTxtAndOrder_AndMakesNamesUnique()
    {
        var a = _store.Create("Rock", [Song("a.mid"), Song("b.kar")]);
        var b = _store.Create("Rock");
        var c = _store.Create("Jazz");
        Assert.Equal("Rock", a);
        Assert.Equal("Rock (2)", b);
        Assert.Equal(["Rock", "Rock (2)", "Jazz"], _store.GetPlaylistNames());
        Assert.True(File.Exists(Path.Combine(_store.FolderPath, "Rock.txt")));
        Assert.Equal(["Rock", "Rock (2)", "Jazz"], File.ReadAllLines(Path.Combine(_store.FolderPath, PlaylistStore.OrderFileName)));
        Assert.Equal([Song("a.mid"), Song("b.kar")], _store.GetEntries("rock")); // case-insensitive lookup
    }

    [Fact]
    public void Names_AreSanitized_AndReservedNamesAvoided()
    {
        var n = _store.Create("a/b:c?");
        Assert.Equal("a_b_c_", n);
        var r = _store.Create(PlaylistStore.RecentlyPlayedName);
        Assert.NotEqual(PlaylistStore.RecentlyPlayedName, r);
        var o = _store.Create("AllPlaylists");
        Assert.NotEqual("AllPlaylists", o);
        Assert.DoesNotContain(PlaylistStore.RecentlyPlayedName, _store.GetPlaylistNames());
    }

    [Fact]
    public void Order_FollowsAllPlaylistsTxt_ThenUnlistedFilesAlphabetically()
    {
        File.WriteAllText(Path.Combine(_store.FolderPath, "Zeta.txt"), "");
        File.WriteAllText(Path.Combine(_store.FolderPath, "Alpha.txt"), "");
        File.WriteAllText(Path.Combine(_store.FolderPath, "Mid.txt"), "");
        File.WriteAllLines(Path.Combine(_store.FolderPath, PlaylistStore.OrderFileName), ["Mid", "Missing", "zeta.txt", "# comment"]);
        Assert.Equal(["Mid", "Zeta", "Alpha"], _store.GetPlaylistNames());
    }

    [Fact]
    public void Rename_Delete_Move()
    {
        _store.Create("One", [Song("1.mid")]);
        _store.Create("Two");
        _store.Create("Three");
        var renamed = _store.Rename("One", "First");
        Assert.Equal("First", renamed);
        Assert.Equal(["First", "Two", "Three"], _store.GetPlaylistNames());
        Assert.Equal([Song("1.mid")], _store.GetEntries("First"));

        Assert.Equal("first", _store.Rename("First", "first")); // case-only rename
        Assert.Equal(["first", "Two", "Three"], _store.GetPlaylistNames());

        _store.Move("Three", 0);
        Assert.Equal(["Three", "first", "Two"], _store.GetPlaylistNames());

        _store.Delete("Two");
        Assert.Equal(["Three", "first"], _store.GetPlaylistNames());
        Assert.False(File.Exists(Path.Combine(_store.FolderPath, "Two.txt")));
        Assert.Empty(_store.GetEntries("Two"));
    }

    [Fact]
    public void Rename_ToExistingName_GetsSuffix()
    {
        _store.Create("A");
        _store.Create("B");
        Assert.Equal("B (2)", _store.Rename("A", "B"));
    }

    [Fact]
    public void Entries_IgnoreCommentsAndBlankLines_AndResolveRelativePaths()
    {
        File.WriteAllLines(Path.Combine(_store.FolderPath, "List.txt"),
            ["# a comment", "", @"C:\Music\x.mid", "  \"C:\\Music\\quoted.kar\"  ", @"sub\rel.mid"]);
        var entries = _store.GetEntries("List");
        Assert.Equal([@"C:\Music\x.mid", @"C:\Music\quoted.kar", Path.Combine(_store.FolderPath, "sub", "rel.mid")], entries);
    }

    [Fact]
    public void RecentlyPlayed_MostRecentFirst_Deduplicated_CappedAt100()
    {
        for (var i = 0; i < 120; i++) _store.AddToRecent(Song($"{i}.mid"));
        _store.AddToRecent(Song("50.mid"));
        var recent = _store.GetRecent();
        Assert.Equal(PlaylistStore.RecentCap, recent.Count);
        Assert.Equal(Song("50.mid"), recent[0]);
        Assert.Equal(Song("119.mid"), recent[1]);
        Assert.Single(recent, p => p == Song("50.mid"));
        Assert.True(File.Exists(Path.Combine(_store.FolderPath, "Recently Played.txt")));
        Assert.DoesNotContain(PlaylistStore.RecentlyPlayedName, _store.GetPlaylistNames());
        _store.ClearRecent();
        Assert.Empty(_store.GetRecent());
    }

    [Fact]
    public void Search_FindsAcrossPlaylists_CaseInsensitive()
    {
        _store.Create("Rock", [Song("Highway Song.mid"), Song("other.mid")]);
        _store.Create("Party", [Song("highway SONG remix.kar")]);
        _store.AddToRecent(Song("Highway Song.mid"));
        var hits = _store.Search("highway song");
        Assert.Equal(2, hits.Count);
        Assert.Equal(["Rock", "Party"], hits.Select(h => h.Playlist));
        Assert.Equal(3, _store.Search("highway song", includeRecent: true).Count);
        Assert.Empty(_store.Search("   "));
    }

    [Fact]
    public void AddFiles_Sort_Randomize()
    {
        _store.Create("L", [Song("c.mid"), Song("a.mid")]);
        _store.AddFiles("L", [Song("b.mid")], insertAt: 1);
        Assert.Equal([Song("c.mid"), Song("b.mid"), Song("a.mid")], _store.GetEntries("L"));
        _store.SortAlphabetically("L");
        Assert.Equal([Song("a.mid"), Song("b.mid"), Song("c.mid")], _store.GetEntries("L"));
        _store.Randomize("L", new Random(1));
        Assert.Equal(3, _store.GetEntries("L").Count);
        Assert.Equal(_store.GetEntries("L").OrderBy(x => x), [Song("a.mid"), Song("b.mid"), Song("c.mid")]);
    }

    [Fact]
    public void Import_Txt_M3u_M3u8()
    {
        var m3u = _dir.File("party.m3u");
        File.WriteAllLines(m3u, ["#EXTM3U", "#EXTINF:123,Some title", @"C:\Music\one.mid", "two.kar", "http://example.invalid/stream.mid"], Encoding.Latin1);
        var m3u8 = _dir.File("utf.m3u8");
        File.WriteAllText(m3u8, "\uFEFF#EXTM3U\r\nC:\\Música\\canción.mid\r\n", new UTF8Encoding(false));
        var txt = _dir.File("plain.txt");
        File.WriteAllLines(txt, [@"C:\Music\x.smf"]);

        Assert.Equal("party", _store.Import(m3u));
        Assert.Equal([@"C:\Music\one.mid", Path.Combine(_dir.Path, "two.kar")], _store.GetEntries("party"));
        Assert.Equal("utf", _store.Import(m3u8));
        Assert.Equal([@"C:\Música\canción.mid"], _store.GetEntries("utf"));
        Assert.Equal("plain", _store.Import(txt));
        Assert.Equal(["party", "utf", "plain"], _store.GetPlaylistNames());
    }

    [Fact]
    public void Import_LegacyJpl_BestEffort_Utf16AndAnsi()
    {
        var jpl = _dir.File("old.jpl");
        File.WriteAllText(jpl, "[Playlist]\r\nfile1=C:\\Old Songs\\tune one.mid|120|x\r\nnot a song\r\nC:\\Old Songs\\tune2.KAR\r\n", Encoding.Unicode);
        Assert.Equal("old", _store.Import(jpl));
        Assert.Equal([@"C:\Old Songs\tune one.mid", @"C:\Old Songs\tune2.KAR"], _store.GetEntries("old"));

        var ansi = _dir.File("ansi.jpl");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        File.WriteAllBytes(ansi, Encoding.GetEncoding(1252).GetBytes("C:\\Müsik\\lied.mid\r\n"));
        _store.Import(ansi);
        Assert.Equal([@"C:\Müsik\lied.mid"], _store.GetEntries("ansi"));
    }

    [Fact]
    public void Export_And_ExportAll()
    {
        _store.Create("A", [Song("1.mid")]);
        _store.Create("B", [Song("2.mid")]);
        var m3u = _dir.File("a.m3u");
        _store.Export("A", m3u);
        Assert.Equal(["#EXTM3U", Song("1.mid")], File.ReadAllLines(m3u));

        var outDir = Path.Combine(_dir.Path, "export");
        Assert.Equal(2, _store.ExportAll(outDir));
        Assert.Equal(["A", "B"], File.ReadAllLines(Path.Combine(outDir, PlaylistStore.OrderFileName)));
        Assert.Equal([Song("2.mid")], File.ReadAllLines(Path.Combine(outDir, "B.txt")));
    }

    [Fact]
    public void ImportFolder_RespectsOrder_AndMergesRecent()
    {
        var src = Path.Combine(_dir.Path, "legacy");
        Directory.CreateDirectory(src);
        File.WriteAllLines(Path.Combine(src, "Second.txt"), [Song("s.mid")]);
        File.WriteAllLines(Path.Combine(src, "First.txt"), [Song("f.mid")]);
        File.WriteAllLines(Path.Combine(src, "Recently Played.txt"), [Song("r.mid")]);
        File.WriteAllLines(Path.Combine(src, PlaylistStore.OrderFileName), ["First.txt", "Second.txt"]);

        var created = _store.ImportFolder(src);
        Assert.Equal(["First", "Second"], created);
        Assert.Equal(["First", "Second"], _store.GetPlaylistNames());
        Assert.Equal([Song("r.mid")], _store.GetRecent());
    }

    [Fact]
    public void ExpandSongPaths_FiltersExtensions_AndRecursesFolders()
    {
        var music = Path.Combine(_dir.Path, "music");
        Directory.CreateDirectory(Path.Combine(music, "sub"));
        File.WriteAllText(Path.Combine(music, "b.mid"), "");
        File.WriteAllText(Path.Combine(music, "a.KAR"), "");
        File.WriteAllText(Path.Combine(music, "notes.txt"), "");
        File.WriteAllText(Path.Combine(music, "sub", "c.smf"), "");
        File.WriteAllText(Path.Combine(music, "sub", "d.midi"), "");
        var single = Path.Combine(_dir.Path, "single.mid");
        var result = PlaylistStore.ExpandSongPaths([music, single, Path.Combine(_dir.Path, "x.wav")]);
        Assert.Equal(5, result.Count);
        Assert.Equal(single, result[^1]);
        Assert.DoesNotContain(result, p => p.EndsWith(".txt"));
    }

    [Fact]
    public void LegacyFinder_FindsFolderWithOrderFile_AndSkipsExcluded()
    {
        var root = Path.Combine(_dir.Path, "root");
        var legacy = Path.Combine(root, "Vendor", "Player", "Playlists");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, PlaylistStore.OrderFileName), "");
        var own = Path.Combine(root, "OneStopMidi", "Playlists");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, PlaylistStore.OrderFileName), "");

        var found = LegacyPlaylistFinder.Find([root, Path.Combine(_dir.Path, "missing")], exclude: own);
        Assert.Equal([legacy], found);
        Assert.Empty(LegacyPlaylistFinder.Find([Path.Combine(_dir.Path, "missing")], exclude: null));
    }
}
