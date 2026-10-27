using OneStopMidi.App.Settings;
using OneStopMidi.Core;
using Xunit;

namespace OneStopMidi.App.Tests;

public class SettingsTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Missing_File_GivesDefaults()
    {
        var store = new SettingsStore(_dir.File("settings.json"));
        var s = store.Load();
        Assert.True(store.WasMissing);
        Assert.Null(store.LastLoadError);
        Assert.Equal(StartupPlaylistMode.LastUsedPlaylist, s.Startup.Mode);
        Assert.True(s.Startup.AutoLoad);
        Assert.False(s.Startup.AutoPlay);
        Assert.Equal(16, s.Routes.Count);
        Assert.Equal("A10", s.Routes[9]);
        Assert.True(s.DrumChannels[9]);
        Assert.Equal(1, s.DrumChannels.Count(d => d));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"ChaseEvents\": \"maybe\"}")]
    [InlineData("\u0000\u0001garbage")]
    public void Corrupt_File_GivesDefaults_AndKeepsABackup(string content)
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, content);
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.NotNull(store.LastLoadError);
        Assert.True(s.ChaseEvents);
        Assert.Equal(StartupPlaylistMode.LastUsedPlaylist, s.Startup.Mode);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Partial_File_WithNullsAndBadValues_IsRepaired()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, """
            {
              // comments and trailing commas are tolerated
              "Startup": null,
              "Routes": ["B9", "zz", null],
              "DrumChannels": [true],
              "OutputDevices": [],
              "Window": { "Width": "NaN", "TreeWidth": 5 },
              "KeyPreset": null,
              "ChordDisplay": "AutoDetect",
            }
            """);
        var store = new SettingsStore(path);
        var s = store.Load();
        Assert.Null(store.LastLoadError);
        Assert.NotNull(s.Startup);
        Assert.Equal(["B9", "A2", "A3"], s.Routes.Take(3));
        Assert.Equal(16, s.Routes.Count);
        Assert.Equal(16, s.DrumChannels.Count);
        Assert.True(s.DrumChannels[0]);
        Assert.False(s.DrumChannels[9]);
        Assert.Equal([0], s.OutputDevices);
        Assert.Equal(1280, s.Window.Width);
        Assert.Equal(210, s.Window.TreeWidth);
        Assert.Equal("Default", s.KeyPreset);
        Assert.Equal(ChordDisplayMode.AutoDetect, s.ChordDisplay);
    }

    [Fact]
    public void Save_Load_RoundTrip()
    {
        var path = Path.Combine(_dir.Path, "sub", "settings.json");
        var store = new SettingsStore(path);
        var s = new AppSettings().Normalize();
        s.Startup.Mode = StartupPlaylistMode.SpecificPlaylist;
        s.Startup.SpecificPlaylist = @"C:\lists\gig.m3u";
        s.Startup.SongMode = StartupSongMode.ResumeLastSong;
        s.Startup.ForceAutoOn = true;
        s.LastPlaylist = "Rock";
        s.LastSongPath = @"C:\m\a.mid";
        s.Routes[3] = "B12";
        s.KeyBindings = new() { ["Stop"] = ["Space"] };
        s.Window.TreeWidth = 333;
        store.Save(s);

        var back = new SettingsStore(path).Load();
        Assert.Equal(StartupPlaylistMode.SpecificPlaylist, back.Startup.Mode);
        Assert.Equal(@"C:\lists\gig.m3u", back.Startup.SpecificPlaylist);
        Assert.Equal(StartupSongMode.ResumeLastSong, back.Startup.SongMode);
        Assert.True(back.Startup.ForceAutoOn);
        Assert.Equal("Rock", back.LastPlaylist);
        Assert.Equal("B12", back.Routes[3]);
        Assert.Equal(["Space"], back.KeyBindings!["Stop"]);
        Assert.Equal(333, back.Window.TreeWidth);
        var json = File.ReadAllText(path);
        Assert.Contains("\"Mode\": \"SpecificPlaylist\"", json); // enums are written as readable names
        Assert.Contains("\"SongMode\": \"ResumeLastSong\"", json);
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var s = new AppSettings().Normalize();
        var c = s.Clone();
        c.Routes[0] = "B1";
        c.Startup.AutoPlay = true;
        c.Window.Width = 999;
        Assert.Equal("A1", s.Routes[0]);
        Assert.False(s.Startup.AutoPlay);
        Assert.NotEqual(999, s.Window.Width);
    }

    [Theory]
    [InlineData("A1", 0, 0)]
    [InlineData("b9", 1, 8)]
    [InlineData(" D16 ", 3, 15)]
    public void RouteFormat_Parse(string text, int port, int channel)
    {
        Assert.True(ChannelRouteFormat.TryParse(text, out var r));
        Assert.Equal(new ChannelRoute(port, channel), r);
        Assert.Equal(text.Trim().ToUpperInvariant(), ChannelRouteFormat.Format(r));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A0")]
    [InlineData("A17")]
    [InlineData("E1")]
    [InlineData("9")]
    [InlineData(null)]
    public void RouteFormat_Rejects(string? text)
    {
        Assert.False(ChannelRouteFormat.TryParse(text, out _));
    }
}
