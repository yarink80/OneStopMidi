using System.Diagnostics;

namespace OneStopMidi.App.Playlists;

/// <summary>
/// Looks for an existing playlist folder (a folder containing <c>AllPlaylists.txt</c>) from another player,
/// used for the first-run import offer. The search is depth- and time-limited and never throws.
/// </summary>
public static class LegacyPlaylistFinder
{
    public static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>();
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.MyDocuments,
                 })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) roots.Add(p);
        }
        return roots;
    }

    /// <summary>Returns candidate folders (excluding <paramref name="exclude"/>), best first.</summary>
    public static IReadOnlyList<string> Find(IEnumerable<string> roots, string? exclude, int maxDepth = 4, TimeSpan? budget = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = budget ?? TimeSpan.FromSeconds(2);
        var found = new List<string>();
        var excludeFull = exclude is null ? null : Path.GetFullPath(exclude).TrimEnd('\\', '/');

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0)
            {
                if (sw.Elapsed > limit) return found;
                var (dir, depth) = queue.Dequeue();
                try
                {
                    if (excludeFull is not null && dir.TrimEnd('\\', '/').Equals(excludeFull, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (File.Exists(Path.Combine(dir, PlaylistStore.OrderFileName)) && !found.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        found.Add(dir);
                    if (depth >= maxDepth) continue;
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        // Skip noisy folders that never hold playlists.
                        if (name.StartsWith('.') || name.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Temp", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Cache", StringComparison.OrdinalIgnoreCase))
                            continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
                catch
                {
                    // access denied etc.
                }
            }
        }
        return found;
    }
}
