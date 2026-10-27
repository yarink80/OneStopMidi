using OneStopMidi.Core;

namespace OneStopMidi.App.Settings;

public enum StartupPlaylistMode { None, LastUsedPlaylist, SpecificPlaylist }

public enum StartupSongMode { FirstSong, ResumeLastSong }

/// <summary>Save format for "Save": null = keep the file's original format.</summary>
public enum MidiSaveFormat { Original, Format0, Format1 }

public sealed class StartupSettings
{
    public StartupPlaylistMode Mode { get; set; } = StartupPlaylistMode.LastUsedPlaylist;
    /// <summary>For <see cref="StartupPlaylistMode.SpecificPlaylist"/>: a stored playlist name or a .txt/.m3u/.m3u8 path.</summary>
    public string? SpecificPlaylist { get; set; }
    public StartupSongMode SongMode { get; set; } = StartupSongMode.FirstSong;
    public bool AutoLoad { get; set; } = true;
    public bool AutoPlay { get; set; }
    public bool ForcePlaylistModeOn { get; set; }
    public bool ForceAutoOn { get; set; }

    public StartupSettings Clone() => (StartupSettings)MemberwiseClone();
}

public sealed class WindowLayout
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 780;
    public bool Maximized { get; set; }
    public double TreeWidth { get; set; } = 210;
    public double ListWidth { get; set; } = 340;

    public WindowLayout Clone() => (WindowLayout)MemberwiseClone();
}

/// <summary>All persisted settings and session state (settings.json).</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // ---- Various ----
    public bool ChaseEvents { get; set; } = true;
    public bool ShowMidiWarnings { get; set; }
    public bool SpacebarTogglesPauseContinue { get; set; }
    public bool DisplayTrackNames { get; set; } = true;
    public ChordDisplayMode ChordDisplay { get; set; } = ChordDisplayMode.PreferEmbeddedThenDetect;
    /// <summary>Code page for lyrics/text events.</summary>
    public int CharacterCodePage { get; set; } = 1252;
    public MidiSaveFormat SaveFormat { get; set; } = MidiSaveFormat.Original;
    public bool PlayClick { get; set; }

    // ---- Music device / routing ----
    /// <summary>Output device ids per port (index 0 = port A).</summary>
    public List<int> OutputDevices { get; set; } = [0];
    /// <summary>16 routes in "A1".."D16" form, indexed by source channel.</summary>
    public List<string> Routes { get; set; } = DefaultRoutes();

    // ---- Drum channels (excluded from transpose) ----
    public List<bool> DrumChannels { get; set; } = DefaultDrumChannels();

    // ---- Keyboard ----
    public string KeyPreset { get; set; } = "Default";
    /// <summary>Action name → chord strings; null = defaults.</summary>
    public Dictionary<string, List<string>>? KeyBindings { get; set; }

    // ---- Startup ----
    public StartupSettings Startup { get; set; } = new();

    // ---- Session state ----
    public bool PlaylistModeOn { get; set; }
    public bool AutoplayOn { get; set; }
    public bool KaraokeScreenOn { get; set; }
    public string? LastPlaylist { get; set; }
    public string? LastSongPath { get; set; }
    public bool FirstRunCompleted { get; set; }
    public WindowLayout Window { get; set; } = new();

    public static List<string> DefaultRoutes() => Enumerable.Range(1, 16).Select(i => $"A{i}").ToList();

    public static List<bool> DefaultDrumChannels() => Enumerable.Range(0, 16).Select(i => i == 9).ToList();

    /// <summary>Repairs values that may be missing or out of range after loading an old/edited file.</summary>
    public AppSettings Normalize()
    {
        OutputDevices = OutputDevices is { Count: > 0 } ? OutputDevices.Take(4).ToList() : [0];
        Routes ??= DefaultRoutes();
        var routes = new List<string>();
        for (var i = 0; i < 16; i++)
        {
            var r = i < Routes.Count ? Routes[i] : null;
            routes.Add(ChannelRouteFormat.TryParse(r, out _) ? r!.Trim().ToUpperInvariant() : $"A{i + 1}");
        }
        Routes = routes;
        DrumChannels ??= DefaultDrumChannels();
        DrumChannels = Enumerable.Range(0, 16).Select(i => i < DrumChannels.Count && DrumChannels[i]).ToList();
        Startup ??= new StartupSettings();
        Window ??= new WindowLayout();
        if (!Enum.IsDefined(Startup.Mode)) Startup.Mode = StartupPlaylistMode.LastUsedPlaylist;
        if (!Enum.IsDefined(Startup.SongMode)) Startup.SongMode = StartupSongMode.FirstSong;
        if (!Enum.IsDefined(ChordDisplay)) ChordDisplay = ChordDisplayMode.PreferEmbeddedThenDetect;
        if (!Enum.IsDefined(SaveFormat)) SaveFormat = MidiSaveFormat.Original;
        if (CharacterCodePage <= 0) CharacterCodePage = 1252;
        KeyPreset = string.IsNullOrWhiteSpace(KeyPreset) ? "Default" : KeyPreset;
        if (double.IsNaN(Window.Width) || Window.Width < 400) Window.Width = 1280;
        if (double.IsNaN(Window.Height) || Window.Height < 300) Window.Height = 780;
        if (double.IsNaN(Window.TreeWidth) || Window.TreeWidth < 60) Window.TreeWidth = 210;
        if (double.IsNaN(Window.ListWidth) || Window.ListWidth < 80) Window.ListWidth = 340;
        return this;
    }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.OutputDevices = OutputDevices.ToList();
        c.Routes = Routes.ToList();
        c.DrumChannels = DrumChannels.ToList();
        c.KeyBindings = KeyBindings?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        c.Startup = Startup.Clone();
        c.Window = Window.Clone();
        return c;
    }
}

/// <summary>"A1".."P16" routing strings: letter = output port (A = first selected device), number = channel 1-16.</summary>
public static class ChannelRouteFormat
{
    public const int MaxPorts = 4;

    public static string Format(ChannelRoute route) => $"{(char)('A' + route.Port)}{route.Channel + 1}";

    public static bool TryParse(string? text, out ChannelRoute route)
    {
        route = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToUpperInvariant();
        if (s.Length < 2 || s[0] < 'A' || s[0] >= 'A' + MaxPorts) return false;
        if (!int.TryParse(s[1..], out var ch) || ch < 1 || ch > 16) return false;
        route = new ChannelRoute(s[0] - 'A', ch - 1);
        return true;
    }
}
