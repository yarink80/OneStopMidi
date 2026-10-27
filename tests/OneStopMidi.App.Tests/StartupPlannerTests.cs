using OneStopMidi.App.Playlists;
using OneStopMidi.App.Settings;
using OneStopMidi.App.Startup;
using Xunit;

namespace OneStopMidi.App.Tests;

public class StartupPlannerTests
{
    private sealed class FakePlaylists : IPlaylistSource
    {
        public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> GetPlaylistNames() => Lists.Keys.ToList();
        public string? FindName(string name) => Lists.Keys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        public IReadOnlyList<string> GetEntries(string name) => Lists.TryGetValue(name, out var l) ? l : [];
    }

    private readonly FakePlaylists _lists = new();
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _files = new(StringComparer.OrdinalIgnoreCase);

    public StartupPlannerTests()
    {
        _lists.Lists["Rock"] = [@"C:\m\r1.mid", @"C:\m\r2.mid", @"C:\m\r3.mid"];
        _lists.Lists["Party"] = [@"C:\m\p1.kar", @"C:\m\p2.kar"];
        _lists.Lists["Empty"] = [];
        foreach (var l in _lists.Lists.Values) foreach (var p in l) _existing.Add(p);
    }

    private StartupPlanner Planner() => new(_lists, p => _existing.Contains(p) || _files.ContainsKey(p),
        p => _files.TryGetValue(p, out var l) ? l : throw new FileNotFoundException(p));

    private static AppSettings Settings(Action<AppSettings>? configure = null)
    {
        var s = new AppSettings().Normalize();
        configure?.Invoke(s);
        return s;
    }

    private static CommandLineOptions Args(params string[] args) => CommandLineOptions.Parse(args);

    [Fact]
    public void Default_IsLastUsedPlaylist_FirstSong_AutoLoad_NoAutoPlay()
    {
        var s = Settings(x => x.LastPlaylist = "rock");
        var plan = Planner().Plan(s, Args());
        Assert.Equal(StartupPlaylistKind.Stored, plan.Kind);
        Assert.Equal("Rock", plan.PlaylistName);
        Assert.Equal(@"C:\m\r1.mid", plan.SongToSelect);
        Assert.Equal(0, plan.SongIndex);
        Assert.True(plan.LoadSong);
        Assert.False(plan.PlaySong);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ModeNone_SelectsNothing()
    {
        var s = Settings(x =>
        {
            x.LastPlaylist = "Rock";
            x.Startup.Mode = StartupPlaylistMode.None;
        });
        var plan = Planner().Plan(s, Args());
        Assert.Equal(StartupPlaylistKind.None, plan.Kind);
        Assert.Null(plan.SongToSelect);
        Assert.False(plan.LoadSong);
    }

    [Fact]
    public void LastUsed_Missing_WarnsAndSelectsNothing()
    {
        var s = Settings(x => x.LastPlaylist = "Deleted list");
        var plan = Planner().Plan(s, Args());
        Assert.Equal(StartupPlaylistKind.None, plan.Kind);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void LastUsed_NeverSet_IsSilent()
    {
        var plan = Planner().Plan(Settings(), Args());
        Assert.Equal(StartupPlaylistKind.None, plan.Kind);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ResumeLastSong_SelectsIt_AndAutoPlays()
    {
        var s = Settings(x =>
        {
            x.LastPlaylist = "Rock";
            x.LastSongPath = @"c:\M\R2.MID";
            x.Startup.SongMode = StartupSongMode.ResumeLastSong;
            x.Startup.AutoPlay = true;
        });
        var plan = Planner().Plan(s, Args());
        Assert.Equal(@"C:\m\r2.mid", plan.SongToSelect);
        Assert.Equal(1, plan.SongIndex);
        Assert.True(plan.LoadSong);
        Assert.True(plan.PlaySong);
    }

    [Fact]
    public void ResumeLastSong_NotInList_FallsBackToFirst_WithWarning()
    {
        var s = Settings(x =>
        {
            x.LastPlaylist = "Rock";
            x.LastSongPath = @"C:\elsewhere\x.mid";
            x.Startup.SongMode = StartupSongMode.ResumeLastSong;
        });
        var plan = Planner().Plan(s, Args());
        Assert.Equal(@"C:\m\r1.mid", plan.SongToSelect);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void FirstSong_SkipsMissingFiles()
    {
        _existing.Remove(@"C:\m\r1.mid");
        var plan = Planner().Plan(Settings(x => x.LastPlaylist = "Rock"), Args());
        Assert.Equal(@"C:\m\r2.mid", plan.SongToSelect);
    }

    [Fact]
    public void AutoLoadOff_SelectsButDoesNotLoadOrPlay()
    {
        var s = Settings(x =>
        {
            x.LastPlaylist = "Rock";
            x.Startup.AutoLoad = false;
            x.Startup.AutoPlay = true;
        });
        var plan = Planner().Plan(s, Args());
        Assert.Equal(@"C:\m\r1.mid", plan.SongToSelect);
        Assert.False(plan.LoadSong);
        Assert.False(plan.PlaySong);
    }

    [Fact]
    public void EmptyPlaylist_SelectsListButNoSong()
    {
        var plan = Planner().Plan(Settings(x => x.LastPlaylist = "Empty"), Args());
        Assert.Equal("Empty", plan.PlaylistName);
        Assert.Null(plan.SongToSelect);
        Assert.False(plan.LoadSong);
    }

    [Fact]
    public void SpecificPlaylist_ByName()
    {
        var s = Settings(x =>
        {
            x.LastPlaylist = "Rock";
            x.Startup.Mode = StartupPlaylistMode.SpecificPlaylist;
            x.Startup.SpecificPlaylist = "party";
        });
        var plan = Planner().Plan(s, Args());
        Assert.Equal(StartupPlaylistKind.Stored, plan.Kind);
        Assert.Equal("Party", plan.PlaylistName);
        Assert.Equal(@"C:\m\p1.kar", plan.SongToSelect);
    }

    [Fact]
    public void SpecificPlaylist_ExternalM3uFile()
    {
        _files[@"D:\lists\gig.m3u"] = [@"D:\gig\a.mid", @"D:\gig\b.mid"];
        _existing.Add(@"D:\gig\a.mid");
        var s = Settings(x =>
        {
            x.Startup.Mode = StartupPlaylistMode.SpecificPlaylist;
            x.Startup.SpecificPlaylist = @"D:\lists\gig.m3u";
            x.Startup.AutoPlay = true;
        });
        var plan = Planner().Plan(s, Args());
        Assert.Equal(StartupPlaylistKind.ExternalFile, plan.Kind);
        Assert.Equal("gig", plan.PlaylistName);
        Assert.Equal(@"D:\lists\gig.m3u", plan.ExternalPlaylistPath);
        Assert.Equal([@"D:\gig\a.mid", @"D:\gig\b.mid"], plan.Entries);
        Assert.Equal(@"D:\gig\a.mid", plan.SongToSelect);
        Assert.True(plan.PlaySong);
    }

    [Fact]
    public void SpecificPlaylist_NotChosen_Warns()
    {
        var s = Settings(x => x.Startup.Mode = StartupPlaylistMode.SpecificPlaylist);
        var plan = Planner().Plan(s, Args());
        Assert.Equal(StartupPlaylistKind.None, plan.Kind);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void CommandLinePlaylist_OverridesSetting()
    {
        var s = Settings(x => x.LastPlaylist = "Rock");
        var plan = Planner().Plan(s, Args("--playlist", "Party"));
        Assert.Equal("Party", plan.PlaylistName);
        Assert.True(plan.LoadSong);
    }

    [Fact]
    public void CommandLinePlaylist_TxtNameOfStoredList()
    {
        var plan = Planner().Plan(Settings(), Args("--playlist=Party.txt"));
        Assert.Equal(StartupPlaylistKind.Stored, plan.Kind);
        Assert.Equal("Party", plan.PlaylistName);
    }

    [Fact]
    public void CommandLinePlaylist_Unknown_FallsBackToSetting_WithWarning()
    {
        var s = Settings(x => x.LastPlaylist = "Rock");
        var plan = Planner().Plan(s, Args("--playlist", "Nope"));
        Assert.Equal("Rock", plan.PlaylistName);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void CommandLineFiles_MakeTemporaryList_LoadAndPlayFirst_EvenWithoutAutoPlay()
    {
        var s = Settings(x =>
        {
            x.LastPlaylist = "Rock";
            x.Startup.AutoPlay = false;
            x.Startup.AutoLoad = false;
        });
        var plan = Planner().Plan(s, Args(@"C:\x\a.mid", @"C:\x\b.kar", "--playlist", "Party"));
        Assert.Equal(StartupPlaylistKind.CommandLineFiles, plan.Kind);
        Assert.Equal(StartupPlanner.CommandLineListName, plan.PlaylistName);
        Assert.Equal([@"C:\x\a.mid", @"C:\x\b.kar"], plan.Entries);
        Assert.Equal(@"C:\x\a.mid", plan.SongToSelect);
        Assert.True(plan.LoadSong);
        Assert.True(plan.PlaySong);
    }

    [Fact]
    public void ForceFlags_TurnModesOn_AndPersistedModesAreKept()
    {
        var plan = Planner().Plan(Settings(x =>
        {
            x.Startup.ForcePlaylistModeOn = true;
            x.Startup.ForceAutoOn = true;
        }), Args());
        Assert.True(plan.PlaylistModeOn);
        Assert.True(plan.AutoOn);

        var plan2 = Planner().Plan(Settings(x => x.PlaylistModeOn = true), Args());
        Assert.True(plan2.PlaylistModeOn);
        Assert.False(plan2.AutoOn);
    }
}

public class CommandLineOptionsTests
{
    [Fact]
    public void Parse_Variants()
    {
        Assert.True(CommandLineOptions.Parse([]).IsEmpty);
        Assert.Equal("My List", CommandLineOptions.Parse(["--playlist", "My List"]).Playlist);
        Assert.Equal("x", CommandLineOptions.Parse(["-p", "x"]).Playlist);
        Assert.Equal("x", CommandLineOptions.Parse(["/playlist", "x"]).Playlist);
        Assert.Equal(@"C:\a b\l.m3u", CommandLineOptions.Parse([@"--playlist=C:\a b\l.m3u"]).Playlist);
        Assert.Equal(@"C:\l.m3u8", CommandLineOptions.Parse([@"C:\l.m3u8"]).Playlist);

        var o = CommandLineOptions.Parse(["a.mid", "--verbose", "b.kar", "--playlist"]);
        Assert.Equal(["a.mid", "b.kar"], o.Files);
        Assert.Equal(["--verbose"], o.Unknown);
        Assert.Null(o.Playlist);
    }
}
