using OneStopMidi.App.Playlists;

namespace OneStopMidi.App.Startup;

/// <summary>
/// Parsed command line. Supported:
/// <c>--playlist &lt;file|name&gt;</c> (also <c>-p</c>, <c>/playlist</c>, <c>--playlist=x</c>),
/// bare song files/folders (loaded into a temporary "Command line" list; the first one is played),
/// and a bare .txt/.m3u/.m3u8/.jpl path (treated as --playlist).
/// </summary>
public sealed class CommandLineOptions
{
    public string? Playlist { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public IReadOnlyList<string> Unknown { get; init; } = [];

    public bool IsEmpty => Playlist is null && Files.Count == 0;

    public static CommandLineOptions Parse(IReadOnlyList<string>? args)
    {
        string? playlist = null;
        var files = new List<string>();
        var unknown = new List<string>();
        if (args is null) return new CommandLineOptions();

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (string.IsNullOrWhiteSpace(a)) continue;
            var lower = a.ToLowerInvariant();
            if (lower is "--playlist" or "-p" or "/playlist" or "/p")
            {
                if (i + 1 < args.Count) playlist = args[++i];
                continue;
            }
            if (lower.StartsWith("--playlist=") || lower.StartsWith("/playlist:"))
            {
                playlist = a[(a.IndexOfAny(['=', ':']) + 1)..];
                continue;
            }
            if (a.StartsWith("--") || (a.StartsWith('-') && a.Length <= 3))
            {
                unknown.Add(a);
                continue;
            }
            if (PlaylistStore.IsPlaylistFile(a) && !PlaylistStore.HasSongExtension(a))
            {
                playlist ??= a;
                continue;
            }
            files.Add(a);
        }
        return new CommandLineOptions { Playlist = string.IsNullOrWhiteSpace(playlist) ? null : playlist.Trim(), Files = files, Unknown = unknown };
    }
}
