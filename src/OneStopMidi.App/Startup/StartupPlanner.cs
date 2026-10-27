using OneStopMidi.App.Playlists;
using OneStopMidi.App.Settings;

namespace OneStopMidi.App.Startup;

public enum StartupPlaylistKind
{
    /// <summary>Nothing to select.</summary>
    None,
    /// <summary>A playlist from the store (<see cref="StartupPlan.PlaylistName"/>).</summary>
    Stored,
    /// <summary>An external .txt/.m3u/.m3u8 file shown as a temporary list (<see cref="StartupPlan.Entries"/>).</summary>
    ExternalFile,
    /// <summary>Song files given on the command line, shown as the temporary "Command line" list.</summary>
    CommandLineFiles,
}

/// <summary>What the main window should do once it is shown.</summary>
public sealed record StartupPlan
{
    public StartupPlaylistKind Kind { get; init; }
    /// <summary>Stored playlist name, or the display name of the temporary list.</summary>
    public string? PlaylistName { get; init; }
    /// <summary>Source file for <see cref="StartupPlaylistKind.ExternalFile"/>.</summary>
    public string? ExternalPlaylistPath { get; init; }
    /// <summary>Entries of the temporary list (ExternalFile / CommandLineFiles); empty for stored lists.</summary>
    public IReadOnlyList<string> Entries { get; init; } = [];
    /// <summary>Song to select in the list (null = none).</summary>
    public string? SongToSelect { get; init; }
    public int SongIndex { get; init; } = -1;
    public bool LoadSong { get; init; }
    public bool PlaySong { get; init; }
    public bool PlaylistModeOn { get; init; }
    public bool AutoOn { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Pure decision logic for "what happens at startup": combines the Startup settings, the command line and the
/// playlist store state into a <see cref="StartupPlan"/>. File-system access is injected for testability.
/// </summary>
public sealed class StartupPlanner
{
    public const string CommandLineListName = "Command line";

    private readonly IPlaylistSource _playlists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IReadOnlyList<string>> _readPlaylistFile;

    public StartupPlanner(IPlaylistSource playlists, Func<string, bool>? fileExists = null,
        Func<string, IReadOnlyList<string>>? readPlaylistFile = null)
    {
        _playlists = playlists;
        _fileExists = fileExists ?? File.Exists;
        _readPlaylistFile = readPlaylistFile ?? (p => PlaylistStore.ReadPlaylistFile(p));
    }

    public StartupPlan Plan(AppSettings settings, CommandLineOptions args)
    {
        var s = settings.Startup ?? new StartupSettings();
        var warnings = new List<string>();
        var playlistMode = settings.PlaylistModeOn || s.ForcePlaylistModeOn;
        var autoOn = settings.AutoplayOn || s.ForceAutoOn;

        // 1) Song files on the command line win: temporary list, load + play the first file.
        if (args.Files.Count > 0)
        {
            var first = args.Files[0];
            return new StartupPlan
            {
                Kind = StartupPlaylistKind.CommandLineFiles,
                PlaylistName = CommandLineListName,
                Entries = args.Files.ToList(),
                SongToSelect = first,
                SongIndex = 0,
                LoadSong = true,
                PlaySong = true,
                PlaylistModeOn = playlistMode,
                AutoOn = autoOn,
                Warnings = warnings,
            };
        }

        // 2) Which playlist?
        (StartupPlaylistKind Kind, string? Name, string? Path, IReadOnlyList<string> Entries)? source = null;
        if (args.Playlist is not null)
        {
            source = Resolve(args.Playlist, warnings);
            if (source is null) warnings.Add($"Playlist '{args.Playlist}' from the command line was not found; using the startup setting instead.");
        }

        if (source is null)
        {
            switch (s.Mode)
            {
                case StartupPlaylistMode.None:
                    break;
                case StartupPlaylistMode.LastUsedPlaylist:
                    if (!string.IsNullOrWhiteSpace(settings.LastPlaylist))
                    {
                        var name = _playlists.FindName(settings.LastPlaylist);
                        if (name is not null) source = (StartupPlaylistKind.Stored, name, null, _playlists.GetEntries(name));
                        else warnings.Add($"Last used playlist '{settings.LastPlaylist}' no longer exists.");
                    }
                    break;
                case StartupPlaylistMode.SpecificPlaylist:
                    if (string.IsNullOrWhiteSpace(s.SpecificPlaylist))
                        warnings.Add("No startup playlist has been chosen.");
                    else
                    {
                        source = Resolve(s.SpecificPlaylist, warnings);
                        if (source is null) warnings.Add($"Startup playlist '{s.SpecificPlaylist}' was not found.");
                    }
                    break;
            }
        }

        if (source is null)
            return new StartupPlan { Kind = StartupPlaylistKind.None, PlaylistModeOn = playlistMode, AutoOn = autoOn, Warnings = warnings };

        var (kind, plName, path, entries) = source.Value;

        // 3) Which song?
        var index = -1;
        if (entries.Count > 0)
        {
            if (s.SongMode == StartupSongMode.ResumeLastSong && !string.IsNullOrWhiteSpace(settings.LastSongPath))
            {
                index = IndexOf(entries, settings.LastSongPath);
                if (index < 0) warnings.Add("The last played song is not in this playlist; selecting the first song.");
            }
            if (index < 0)
            {
                index = FirstExisting(entries);
            }
        }
        var song = index >= 0 ? entries[index] : null;
        var load = s.AutoLoad && song is not null;
        return new StartupPlan
        {
            Kind = kind,
            PlaylistName = plName,
            ExternalPlaylistPath = path,
            Entries = kind == StartupPlaylistKind.Stored ? [] : entries,
            SongToSelect = song,
            SongIndex = index,
            LoadSong = load,
            PlaySong = load && s.AutoPlay,
            PlaylistModeOn = playlistMode,
            AutoOn = autoOn,
            Warnings = warnings,
        };
    }

    private (StartupPlaylistKind, string?, string?, IReadOnlyList<string>)? Resolve(string nameOrPath, List<string> warnings)
    {
        var stored = _playlists.FindName(nameOrPath);
        // A stored playlist name wins unless the argument clearly is a path to an existing file.
        var looksLikePath = nameOrPath.Contains('\\') || nameOrPath.Contains('/') || PlaylistStore.IsPlaylistFile(nameOrPath);
        if (looksLikePath && _fileExists(nameOrPath))
        {
            try
            {
                var entries = _readPlaylistFile(nameOrPath);
                return (StartupPlaylistKind.ExternalFile, Path.GetFileNameWithoutExtension(nameOrPath), nameOrPath, entries);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read playlist file '{nameOrPath}': {ex.Message}");
                return null;
            }
        }
        if (stored is null && looksLikePath)
        {
            // "Foo.txt" may also name a stored playlist.
            stored = _playlists.FindName(Path.GetFileNameWithoutExtension(nameOrPath));
        }
        return stored is null ? null : (StartupPlaylistKind.Stored, stored, null, _playlists.GetEntries(stored));
    }

    private static int IndexOf(IReadOnlyList<string> entries, string path)
    {
        for (var i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i], path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private int FirstExisting(IReadOnlyList<string> entries)
    {
        for (var i = 0; i < entries.Count; i++)
            if (_fileExists(entries[i])) return i;
        return 0;
    }
}
