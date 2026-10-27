using System.Text.Json;
using System.Windows.Input;

namespace OneStopMidi.App.Input;

public enum KeyBindResult { Added, AlreadyBound, Conflict, Invalid }

/// <summary>
/// Action → key chords map. Each action may have several chords; a chord belongs to at most one action.
/// </summary>
public sealed class KeyMap
{
    public const string DefaultPresetName = "Default";
    public const string FootPedalPresetName = "Foot pedal / remote";
    public static IReadOnlyList<string> PresetNames { get; } = [DefaultPresetName, FootPedalPresetName];

    private readonly Dictionary<AppAction, List<KeyChord>> _bindings = new();
    private readonly Dictionary<KeyChord, AppAction> _reverse = new();

    /// <summary>Raised after any change.</summary>
    public event EventHandler? Changed;

    public KeyMap() { }

    // ------------------------------------------------------------------ defaults / presets

    private static readonly (AppAction Action, string[] Chords)[] DefaultTable =
    [
        (AppAction.OpenFile, ["Ctrl+O"]),
        (AppAction.SaveFile, ["Ctrl+S"]),
        (AppAction.SaveFileAs, ["Ctrl+Shift+S"]),
        (AppAction.RevertToSaved, ["Ctrl+R"]),
        (AppAction.OpenSettings, ["Ctrl+,"]),

        (AppAction.Play, ["NumPad0", "M"]),
        (AppAction.Stop, ["NumPadEnter"]),
        (AppAction.StartStop, ["Space"]),
        (AppAction.PauseContinue, ["NumPad."]),
        (AppAction.PreviousBar, ["Left"]),
        (AppAction.NextBar, ["Right"]),
        (AppAction.GoToStart, ["Home"]),
        (AppAction.ToggleLoop, ["C", "Esc"]),
        (AppAction.SetLeftLocator, ["Z"]),
        (AppAction.ResetLeftLocator, ["Shift+Z"]),
        (AppAction.SetRightLocator, ["X"]),
        (AppAction.ResetRightLocator, ["Shift+X"]),

        (AppAction.TempoUp, ["NumPad+"]),
        (AppAction.TempoDown, ["NumPad-"]),
        (AppAction.TempoReset, ["NumPad*"]),
        (AppAction.TransposeUp, ["Ctrl+Up"]),
        (AppAction.TransposeDown, ["Ctrl+Down"]),

        (AppAction.ToggleClick, ["Ctrl+M"]),
        (AppAction.ToggleAutoplay, ["A"]),
        (AppAction.TogglePlaylistMode, ["J"]),
        (AppAction.ToggleLyrics, ["K"]),

        (AppAction.Undo, ["Ctrl+Z"]),
        (AppAction.Redo, ["Ctrl+Y"]),
        (AppAction.Cut, ["Ctrl+X"]),
        (AppAction.Copy, ["Ctrl+C"]),
        (AppAction.Paste, ["Ctrl+V"]),
        (AppAction.DeleteRange, ["Ctrl+Backspace"]),
        (AppAction.EraseMutedChannels, ["Ctrl+E"]),
        (AppAction.DeleteListItem, ["Delete", "Backspace"]),

        (AppAction.SelectPreviousSong, ["Up"]),
        (AppAction.SelectNextSong, ["Down"]),
        (AppAction.LoadAndPlaySelectedSong, ["Enter"]),
        (AppAction.PreviousSong, ["Ctrl+Left"]),
        (AppAction.NextSong, ["Ctrl+Right"]),
        (AppAction.PreviousPlaylist, ["PageUp"]),
        (AppAction.NextPlaylist, ["PageDown"]),

        (AppAction.NewPlaylist, ["Ctrl+Shift+N"]),
        (AppAction.RenamePlaylist, ["F2"]),
        (AppAction.SearchPlaylists, ["Ctrl+F"]),

        (AppAction.ShowKeyboardShortcuts, ["F1"]),
    ];

    /// <summary>Overrides applied on top of the defaults for the foot pedal / remote preset.</summary>
    private static readonly (AppAction Action, string Chord)[] FootPedalOverrides =
    [
        (AppAction.PreviousSong, "Left"),
        (AppAction.NextSong, "Right"),
        (AppAction.TempoUp, "Up"),
        (AppAction.TempoDown, "Down"),
        (AppAction.PauseContinue, "Enter"),
        (AppAction.Stop, "Space"),
    ];

    public static KeyMap CreateDefault()
    {
        var map = new KeyMap();
        map.FillDefaults();
        return map;
    }

    /// <summary>Creates a fresh map for a preset name; unknown names give the default map.</summary>
    public static KeyMap CreatePreset(string? presetName)
    {
        var map = CreateDefault();
        if (string.Equals(presetName, FootPedalPresetName, StringComparison.OrdinalIgnoreCase))
        {
            map.SuspendEvents(() =>
            {
                foreach (var (action, chord) in FootPedalOverrides)
                    map.Reassign(action, KeyChord.Parse(chord));
            });
        }
        return map;
    }

    public static IReadOnlyList<KeyChord> GetDefaultChords(AppAction action)
    {
        foreach (var (a, chords) in DefaultTable)
            if (a == action) return chords.Select(KeyChord.Parse).ToList();
        return [];
    }

    private void FillDefaults()
    {
        _bindings.Clear();
        _reverse.Clear();
        foreach (var (action, chords) in DefaultTable)
            foreach (var c in chords)
                AddCore(action, KeyChord.Parse(c));
    }

    /// <summary>Replace all bindings with those of the preset.</summary>
    public void ApplyPreset(string? presetName) => CopyFrom(CreatePreset(presetName));

    public void ResetToDefaults() => CopyFrom(CreateDefault());

    /// <summary>Restore the default chords of one action (stealing them from other actions if needed).</summary>
    public void ResetAction(AppAction action)
    {
        SuspendEvents(() =>
        {
            ClearAction(action);
            foreach (var c in GetDefaultChords(action)) Reassign(action, c);
        });
        OnChanged();
    }

    // ------------------------------------------------------------------ queries

    public IReadOnlyList<KeyChord> GetChords(AppAction action) =>
        _bindings.TryGetValue(action, out var list) ? list.ToList() : [];

    public bool TryGetAction(KeyChord chord, out AppAction action) => _reverse.TryGetValue(chord, out action);

    /// <summary>The action (other than <paramref name="forAction"/>) that already owns <paramref name="chord"/>.</summary>
    public AppAction? FindConflict(KeyChord chord, AppAction forAction) =>
        _reverse.TryGetValue(chord, out var owner) && owner != forAction ? owner : null;

    public IEnumerable<(AppAction Action, KeyChord Chord)> AllBindings()
    {
        foreach (var action in AppActionInfo.All)
            if (_bindings.TryGetValue(action, out var list))
                foreach (var c in list)
                    yield return (action, c);
    }

    public string FormatChords(AppAction action) => string.Join(", ", GetChords(action).Select(c => c.Format()));

    // ------------------------------------------------------------------ edits

    /// <summary>Adds a chord unless it is invalid, already bound here, or owned by another action (Conflict).</summary>
    public KeyBindResult Add(AppAction action, KeyChord chord)
    {
        if (!chord.IsValid) return KeyBindResult.Invalid;
        if (_reverse.TryGetValue(chord, out var owner))
            return owner == action ? KeyBindResult.AlreadyBound : KeyBindResult.Conflict;
        AddCore(action, chord);
        OnChanged();
        return KeyBindResult.Added;
    }

    /// <summary>Binds the chord to <paramref name="action"/>, removing it from whatever action had it.</summary>
    public void Reassign(AppAction action, KeyChord chord)
    {
        if (!chord.IsValid) return;
        if (_reverse.TryGetValue(chord, out var owner))
        {
            if (owner == action) return;
            RemoveCore(owner, chord);
        }
        AddCore(action, chord);
        OnChanged();
    }

    public bool Remove(AppAction action, KeyChord chord)
    {
        if (!RemoveCore(action, chord)) return false;
        OnChanged();
        return true;
    }

    public void ClearAction(AppAction action)
    {
        if (!_bindings.TryGetValue(action, out var list) || list.Count == 0) return;
        foreach (var c in list) _reverse.Remove(c);
        list.Clear();
        OnChanged();
    }

    public void CopyFrom(KeyMap other)
    {
        _bindings.Clear();
        _reverse.Clear();
        foreach (var (a, c) in other.AllBindings()) AddCore(a, c);
        OnChanged();
    }

    public KeyMap Clone()
    {
        var m = new KeyMap();
        foreach (var (a, c) in AllBindings()) m.AddCore(a, c);
        return m;
    }

    private void AddCore(AppAction action, KeyChord chord)
    {
        if (!_bindings.TryGetValue(action, out var list)) _bindings[action] = list = new List<KeyChord>();
        list.Add(chord);
        _reverse[chord] = action;
    }

    private bool RemoveCore(AppAction action, KeyChord chord)
    {
        if (!_bindings.TryGetValue(action, out var list) || !list.Remove(chord)) return false;
        _reverse.Remove(chord);
        return true;
    }

    private int _suspend;
    private void SuspendEvents(Action body)
    {
        _suspend++;
        try { body(); } finally { _suspend--; }
    }

    private void OnChanged()
    {
        if (_suspend == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>Serializable form: action name → chord strings. Every action is present (empty list = unbound).</summary>
    public Dictionary<string, List<string>> ToDictionary()
    {
        var d = new Dictionary<string, List<string>>();
        foreach (var action in AppActionInfo.All)
            d[action.ToString()] = GetChords(action).Select(c => c.Format()).ToList();
        return d;
    }

    /// <summary>
    /// Builds a map from the serializable form. Unknown action names and unparsable chords are skipped;
    /// a chord listed under two actions stays with the first. Actions missing from the dictionary (e.g. new
    /// in this version) get their default chords when those are still free.
    /// </summary>
    public static KeyMap FromDictionary(IReadOnlyDictionary<string, List<string>>? data)
    {
        if (data is null) return CreateDefault();
        var map = new KeyMap();
        var present = new HashSet<AppAction>();
        foreach (var (name, chords) in data)
        {
            if (!Enum.TryParse<AppAction>(name, ignoreCase: true, out var action) || !Enum.IsDefined(action)) continue;
            present.Add(action);
            if (chords is null) continue;
            foreach (var text in chords)
                if (KeyChord.TryParse(text, out var chord) && !map._reverse.ContainsKey(chord))
                    map.AddCore(action, chord);
        }
        foreach (var action in AppActionInfo.All)
        {
            if (present.Contains(action)) continue;
            foreach (var c in GetDefaultChords(action))
                if (!map._reverse.ContainsKey(c)) map.AddCore(action, c);
        }
        return map;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class KeyMapFile
    {
        public string Format { get; set; } = "onestopmidi-keymap";
        public int Version { get; set; } = 1;
        public Dictionary<string, List<string>>? Bindings { get; set; }
    }

    public string ToJson() =>
        JsonSerializer.Serialize(new KeyMapFile { Bindings = ToDictionary() }, JsonOptions);

    /// <summary>Accepts the export format or a bare { "Action": ["chord", ...] } dictionary.</summary>
    public static KeyMap FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Key map JSON must be an object.");
        var root = doc.RootElement;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("Bindings", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Object)
            {
                root = prop.Value;
                break;
            }
        }
        var dict = new Dictionary<string, List<string>>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            dict[prop.Name] = prop.Value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .ToList();
        }
        if (dict.Count == 0) throw new FormatException("No key bindings found in the JSON.");
        return FromDictionary(dict);
    }

    public void Export(string path) => File.WriteAllText(path, ToJson());

    public static KeyMap Import(string path) => FromJson(File.ReadAllText(path));
}
