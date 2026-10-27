using System.Text;
using System.Text.RegularExpressions;

namespace OneStopMidi.App.Playlists;

public sealed record PlaylistSearchHit(string Playlist, string Path);

/// <summary>Read-only view of the playlists, used by the startup planner.</summary>
public interface IPlaylistSource
{
    IReadOnlyList<string> GetPlaylistNames();
    /// <summary>Case-insensitive lookup of a stored playlist; returns the stored spelling or null.</summary>
    string? FindName(string name);
    IReadOnlyList<string> GetEntries(string name);
}

/// <summary>
/// Playlists stored as UTF-8 text files (one absolute path per line, '#' comments ignored) in one folder,
/// plus an <c>AllPlaylists.txt</c> order file and the special <c>Recently Played.txt</c> list.
/// </summary>
public sealed class PlaylistStore : IPlaylistSource
{
    public const string RecentlyPlayedName = "Recently Played";
    public const string OrderFileName = "AllPlaylists.txt";
    public const int RecentCap = 100;

    public static readonly string[] SongExtensions = [".mid", ".midi", ".kar", ".smf"];
    public static readonly string[] PlaylistFileExtensions = [".txt", ".m3u", ".m3u8", ".jpl"];

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public string FolderPath { get; }

    public static string DefaultFolder => Path.Combine(Common.AppPaths.DataFolder, "Playlists");

    public PlaylistStore(string folderPath)
    {
        FolderPath = Path.GetFullPath(folderPath);
        Directory.CreateDirectory(FolderPath);
    }

    // ------------------------------------------------------------------ names / order

    public static bool IsReservedName(string name) =>
        name.Equals(RecentlyPlayedName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(Path.GetFileNameWithoutExtension(OrderFileName), StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes characters that are invalid in file names; never returns an empty string.</summary>
    public static string SanitizeName(string? name)
    {
        var s = (name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().TrimEnd('.');
        return s.Length == 0 ? "Playlist" : s;
    }

    private string FileFor(string name) => Path.Combine(FolderPath, name + ".txt");
    private string OrderFile => Path.Combine(FolderPath, OrderFileName);

    /// <summary>Playlist names in display order (AllPlaylists.txt first, then any other .txt files A-Z).</summary>
    public IReadOnlyList<string> GetPlaylistNames()
    {
        var existing = Directory.EnumerateFiles(FolderPath, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && !IsReservedName(n))
            .Select(n => n!)
            .ToList();
        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(OrderFile))
        {
            foreach (var raw in ReadLines(OrderFile))
            {
                var n = raw.Trim();
                if (n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
                if (n.Length == 0 || n.StartsWith('#') || !existingSet.Contains(n) || !seen.Add(n)) continue;
                result.Add(existing.First(e => e.Equals(n, StringComparison.OrdinalIgnoreCase)));
            }
        }
        result.AddRange(existing.Where(n => !seen.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private void WriteOrder(IEnumerable<string> names) =>
        File.WriteAllLines(OrderFile, names, Utf8NoBom);

    public bool Exists(string name) => FindName(name) is not null;

    public string? FindName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Equals(RecentlyPlayedName, StringComparison.OrdinalIgnoreCase)) return RecentlyPlayedName;
        return GetPlaylistNames().FirstOrDefault(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Makes a name unique among the stored playlists ("Name", "Name (2)", ...).</summary>
    public string MakeUniqueName(string requested, string? ignore = null)
    {
        var baseName = SanitizeName(requested);
        if (IsReservedName(baseName)) baseName += " (list)";
        var names = new HashSet<string>(GetPlaylistNames(), StringComparer.OrdinalIgnoreCase);
        if (ignore is not null) names.Remove(ignore);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    // ------------------------------------------------------------------ CRUD

    public IReadOnlyList<string> GetEntries(string name)
    {
        var actual = FindName(name);
        if (actual is null) return [];
        var file = FileFor(actual);
        return File.Exists(file) ? ParsePlaylistText(ReadLines(file), FolderPath, legacy: false) : [];
    }

    public void SaveEntries(string name, IEnumerable<string> entries)
    {
        var actual = FindName(name) ?? throw new InvalidOperationException($"Playlist '{name}' does not exist.");
        var list = entries.ToList();
        if (actual == RecentlyPlayedName && list.Count > RecentCap) list = list.Take(RecentCap).ToList();
        File.WriteAllLines(FileFor(actual), list, Utf8NoBom);
    }

    /// <summary>Creates a playlist (unique name, appended to the order) and returns its actual name.</summary>
    public string Create(string requestedName, IEnumerable<string>? entries = null)
    {
        var name = MakeUniqueName(requestedName);
        File.WriteAllLines(FileFor(name), entries ?? [], Utf8NoBom);
        var order = GetPlaylistNames().Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        order.Add(name);
        WriteOrder(order);
        return name;
    }

    public string Rename(string oldName, string newName)
    {
        var actual = FindName(oldName);
        if (actual is null || actual == RecentlyPlayedName) throw new InvalidOperationException($"Cannot rename '{oldName}'.");
        var target = MakeUniqueName(newName, ignore: actual);
        if (target == actual) return actual;
        var order = GetPlaylistNames().ToList();
        var src = FileFor(actual);
        var dst = FileFor(target);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: go through a temporary name.
            var tmp = src + ".renaming";
            File.Move(src, tmp);
            File.Move(tmp, dst);
        }
        else
        {
            File.Move(src, dst);
        }
        WriteOrder(order.Select(n => n.Equals(actual, StringComparison.OrdinalIgnoreCase) ? target : n));
        return target;
    }

    public void Delete(string name)
    {
        var actual = FindName(name);
        if (actual is null) return;
        if (actual == RecentlyPlayedName)
        {
            ClearRecent();
            return;
        }
        var order = GetPlaylistNames().Where(n => !n.Equals(actual, StringComparison.OrdinalIgnoreCase)).ToList();
        File.Delete(FileFor(actual));
        WriteOrder(order);
    }

    /// <summary>Moves a playlist to a new position in the display order.</summary>
    public void Move(string name, int newIndex)
    {
        var order = GetPlaylistNames().ToList();
        var idx = order.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        var item = order[idx];
        order.RemoveAt(idx);
        order.Insert(Math.Clamp(newIndex, 0, order.Count), item);
        WriteOrder(order);
    }

    public void SetOrder(IEnumerable<string> names) => WriteOrder(names);

    // ------------------------------------------------------------------ list content helpers

    public void AddFiles(string name, IEnumerable<string> files, int? insertAt = null)
    {
        var entries = GetEntries(name).ToList();
        var add = files.ToList();
        var at = insertAt is int i ? Math.Clamp(i, 0, entries.Count) : entries.Count;
        entries.InsertRange(at, add);
        SaveEntries(name, entries);
    }

    public void SortAlphabetically(string name) =>
        SaveEntries(name, SortEntries(GetEntries(name)));

    public static List<string> SortEntries(IEnumerable<string> entries) =>
        entries.OrderBy(e => Path.GetFileName(e), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Randomize(string name, Random? rng = null) =>
        SaveEntries(name, Shuffle(GetEntries(name), rng));

    public static List<string> Shuffle(IEnumerable<string> entries, Random? rng = null)
    {
        rng ??= Random.Shared;
        var list = entries.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // ------------------------------------------------------------------ recently played

    public IReadOnlyList<string> GetRecent()
    {
        var file = FileFor(RecentlyPlayedName);
        return File.Exists(file) ? ParsePlaylistText(ReadLines(file), FolderPath, legacy: false) : [];
    }

    /// <summary>Moves (or inserts) the path to the top of Recently Played, capped at <see cref="RecentCap"/>.</summary>
    public void AddToRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var list = GetRecent().Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, path);
        if (list.Count > RecentCap) list.RemoveRange(RecentCap, list.Count - RecentCap);
        File.WriteAllLines(FileFor(RecentlyPlayedName), list, Utf8NoBom);
    }

    public void SaveRecent(IEnumerable<string> entries) =>
        File.WriteAllLines(FileFor(RecentlyPlayedName), entries.Take(RecentCap), Utf8NoBom);

    public void ClearRecent() => File.WriteAllLines(FileFor(RecentlyPlayedName), Array.Empty<string>(), Utf8NoBom);

    // ------------------------------------------------------------------ search

    /// <summary>Case-insensitive search of file names (and full paths) across all playlists.</summary>
    public IReadOnlyList<PlaylistSearchHit> Search(string text, bool includeRecent = false)
    {
        var hits = new List<PlaylistSearchHit>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        var needle = text.Trim();
        var names = GetPlaylistNames().ToList();
        if (includeRecent) names.Insert(0, RecentlyPlayedName);
        foreach (var name in names)
        {
            var entries = name == RecentlyPlayedName ? GetRecent() : GetEntries(name);
            foreach (var e in entries)
                if (e.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
                    hits.Add(new PlaylistSearchHit(name, e));
        }
        return hits;
    }

    // ------------------------------------------------------------------ import / export

    /// <summary>Imports a .txt/.m3u/.m3u8/.jpl file as a new playlist; returns the new name.</summary>
    public string Import(string playlistFile, string? name = null)
    {
        var entries = ReadPlaylistFile(playlistFile);
        return Create(name ?? Path.GetFileNameWithoutExtension(playlistFile), entries);
    }

    /// <summary>
    /// Imports every playlist of a folder that uses the AllPlaylists.txt convention (order respected,
    /// Recently Played merged into ours). Returns the names created.
    /// </summary>
    public IReadOnlyList<string> ImportFolder(string folder)
    {
        var created = new List<string>();
        var files = Directory.EnumerateFiles(folder, "*.txt")
            .Where(f => !Path.GetFileName(f).Equals(OrderFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ordered = new List<string>();
        var orderFile = Path.Combine(folder, OrderFileName);
        if (File.Exists(orderFile))
        {
            foreach (var raw in ReadLines(orderFile))
            {
                var n = raw.Trim();
                if (n.Length == 0 || n.StartsWith('#')) continue;
                if (!n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) n += ".txt";
                var match = files.FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !ordered.Contains(match)) ordered.Add(match);
            }
        }
        ordered.AddRange(files.Where(f => !ordered.Contains(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        foreach (var file in ordered)
        {
            var baseName = Path.GetFileNameWithoutExtension(file);
            if (baseName.Equals(RecentlyPlayedName, StringComparison.OrdinalIgnoreCase))
            {
                var merged = GetRecent().ToList();
                foreach (var p in ReadPlaylistFile(file))
                    if (!merged.Contains(p, StringComparer.OrdinalIgnoreCase)) merged.Add(p);
                SaveRecent(merged);
                continue;
            }
            created.Add(Import(file));
        }
        return created;
    }

    /// <summary>Exports a playlist; .m3u/.m3u8 get an #EXTM3U header, anything else is plain text.</summary>
    public void Export(string name, string destinationPath)
    {
        var entries = name.Equals(RecentlyPlayedName, StringComparison.OrdinalIgnoreCase) ? GetRecent() : GetEntries(name);
        WritePlaylistFile(destinationPath, entries);
    }

    public static void WritePlaylistFile(string destinationPath, IEnumerable<string> entries)
    {
        var ext = Path.GetExtension(destinationPath).ToLowerInvariant();
        var lines = new List<string>();
        if (ext is ".m3u" or ".m3u8") lines.Add("#EXTM3U");
        lines.AddRange(entries);
        var enc = ext == ".m3u" ? Encoding.Latin1 : Utf8NoBom;
        File.WriteAllLines(destinationPath, lines, enc);
    }

    /// <summary>Writes every playlist plus AllPlaylists.txt into a folder; returns the number of playlists.</summary>
    public int ExportAll(string folder)
    {
        Directory.CreateDirectory(folder);
        var names = GetPlaylistNames();
        foreach (var n in names) Export(n, Path.Combine(folder, n + ".txt"));
        File.WriteAllLines(Path.Combine(folder, OrderFileName), names, Utf8NoBom);
        return names.Count;
    }

    // ------------------------------------------------------------------ parsing

    private static readonly Regex LegacySongPath = new(
        @"(?:[A-Za-z]:\\|\\\\|/)[^\t\r\n""|<>*?]*?\.(?:midi|mid|kar|smf)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads a .txt/.m3u/.m3u8/.jpl playlist; relative paths are resolved against its folder.</summary>
    public static List<string> ReadPlaylistFile(string path)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var legacy = Path.GetExtension(path).Equals(".jpl", StringComparison.OrdinalIgnoreCase);
        return ParsePlaylistText(ReadLines(path), baseDir, legacy);
    }

    public static List<string> ParsePlaylistText(IEnumerable<string> lines, string baseDir, bool legacy)
    {
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (legacy)
            {
                var m = LegacySongPath.Match(line);
                if (m.Success) line = m.Value;
                else if (!HasSongExtension(line)) continue;
            }
            line = line.Trim('"');
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out var uri))
                line = uri.LocalPath;
            if (line.Contains("://")) continue; // other URLs are not playable files
            try
            {
                if (!Path.IsPathRooted(line)) line = Path.GetFullPath(Path.Combine(baseDir, line));
            }
            catch
            {
                continue;
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>Reads text with BOM detection, UTF-16 sniffing, strict UTF-8 and a Windows-1252 fallback.</summary>
    public static string[] ReadLines(string path) =>
        DecodeText(File.ReadAllBytes(path)).Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 4)
        {
            int oddZeros = 0, evenZeros = 0, n = Math.Min(bytes.Length, 512);
            for (var i = 0; i < n; i++)
                if (bytes[i] == 0) { if ((i & 1) == 1) oddZeros++; else evenZeros++; }
            if (oddZeros > n / 4 && evenZeros == 0) return Encoding.Unicode.GetString(bytes);
            if (evenZeros > n / 4 && oddZeros == 0) return Encoding.BigEndianUnicode.GetString(bytes);
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return GetAnsiEncoding().GetString(bytes);
        }
    }

    private static Encoding GetAnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch
        {
            return Encoding.Latin1;
        }
    }

    // ------------------------------------------------------------------ file helpers

    public static bool HasSongExtension(string path) =>
        SongExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsPlaylistFile(string path) =>
        PlaylistFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Expands dropped paths: song files are kept, folders are searched recursively (sorted).</summary>
    public static List<string> ExpandSongPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(p, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(HasSongExtension)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                }
                catch
                {
                    // unreadable folder: skip
                }
            }
            else if (HasSongExtension(p))
            {
                result.Add(Path.GetFullPath(p));
            }
        }
        return result;
    }
}
