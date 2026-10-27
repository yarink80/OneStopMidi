namespace OneStopMidi.App.Input;

/// <summary>
/// Every command the user can trigger. Names are persisted in the key map JSON, so never rename a member;
/// append new members instead.
/// </summary>
public enum AppAction
{
    // ---- File ----
    OpenFile,
    SaveFile,
    SaveFileAs,
    RevertToSaved,
    OpenSettings,
    ExitApp,

    // ---- Transport ----
    Play,
    Stop,
    StartStop,
    PauseContinue,
    PreviousBar,
    NextBar,
    GoToStart,
    ToggleLoop,
    SetLeftLocator,
    ResetLeftLocator,
    SetRightLocator,
    ResetRightLocator,
    GoToLeftLocator,

    // ---- Tempo / pitch ----
    TempoUp,
    TempoDown,
    TempoReset,
    TransposeUp,
    TransposeDown,
    TransposeReset,

    // ---- Modes / view ----
    ToggleClick,
    ToggleChaseEvents,
    ToggleAutoplay,
    TogglePlaylistMode,
    ToggleLyrics,

    // ---- Mixer ----
    FullVolume,
    ResetDevices,

    // ---- Edit ----
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    DeleteRange,
    EraseMutedChannels,
    SetSequenceName,
    DeleteListItem,

    // ---- Song list navigation ----
    SelectPreviousSong,
    SelectNextSong,
    LoadSelectedSong,
    LoadAndPlaySelectedSong,
    PreviousSong,
    NextSong,
    PreviousPlaylist,
    NextPlaylist,

    // ---- Playlists ----
    NewPlaylist,
    RenamePlaylist,
    DeletePlaylist,
    ImportPlaylists,
    ExportPlaylist,
    ExportAllPlaylists,
    SearchPlaylists,
    AddFiles,
    AddFolder,
    SortList,
    RandomizeList,
    FindOnComputer,

    // ---- Help ----
    ShowKeyboardShortcuts,
    About,
}

/// <summary>Display metadata for <see cref="AppAction"/>.</summary>
public static class AppActionInfo
{
    private static readonly Dictionary<AppAction, (string Category, string Name)> Info = new()
    {
        [AppAction.OpenFile] = ("File", "Open MIDI file"),
        [AppAction.SaveFile] = ("File", "Save"),
        [AppAction.SaveFileAs] = ("File", "Save as"),
        [AppAction.RevertToSaved] = ("File", "Revert to saved"),
        [AppAction.OpenSettings] = ("File", "Settings"),
        [AppAction.ExitApp] = ("File", "Exit"),

        [AppAction.Play] = ("Transport", "Start (play from beginning / left locator)"),
        [AppAction.Stop] = ("Transport", "Stop"),
        [AppAction.StartStop] = ("Transport", "Start / stop"),
        [AppAction.PauseContinue] = ("Transport", "Pause / continue (play/pause)"),
        [AppAction.PreviousBar] = ("Transport", "Previous bar"),
        [AppAction.NextBar] = ("Transport", "Next bar"),
        [AppAction.GoToStart] = ("Transport", "Go to start"),
        [AppAction.ToggleLoop] = ("Transport", "Loop on/off"),
        [AppAction.SetLeftLocator] = ("Transport", "Set left locator"),
        [AppAction.ResetLeftLocator] = ("Transport", "Reset left locator"),
        [AppAction.SetRightLocator] = ("Transport", "Set right locator"),
        [AppAction.ResetRightLocator] = ("Transport", "Reset right locator"),
        [AppAction.GoToLeftLocator] = ("Transport", "Go to left locator"),

        [AppAction.TempoUp] = ("Tempo / pitch", "Tempo +1%"),
        [AppAction.TempoDown] = ("Tempo / pitch", "Tempo -1%"),
        [AppAction.TempoReset] = ("Tempo / pitch", "Reset tempo change"),
        [AppAction.TransposeUp] = ("Tempo / pitch", "Transpose +1 semitone"),
        [AppAction.TransposeDown] = ("Tempo / pitch", "Transpose -1 semitone"),
        [AppAction.TransposeReset] = ("Tempo / pitch", "Reset transpose"),

        [AppAction.ToggleClick] = ("Modes", "MIDI click on/off"),
        [AppAction.ToggleChaseEvents] = ("Modes", "Chase events on/off"),
        [AppAction.ToggleAutoplay] = ("Modes", "Auto (autoplay) on/off"),
        [AppAction.TogglePlaylistMode] = ("Modes", "Playlist mode on/off"),
        [AppAction.ToggleLyrics] = ("Modes", "Lyrics (karaoke) view on/off"),

        [AppAction.FullVolume] = ("Mixer", "Full volume on all channels"),
        [AppAction.ResetDevices] = ("Mixer", "Reset MIDI devices"),

        [AppAction.Undo] = ("Edit", "Undo"),
        [AppAction.Redo] = ("Edit", "Redo"),
        [AppAction.Cut] = ("Edit", "Cut (range or list items)"),
        [AppAction.Copy] = ("Edit", "Copy (range or list items)"),
        [AppAction.Paste] = ("Edit", "Paste (range or list items)"),
        [AppAction.DeleteRange] = ("Edit", "Delete range"),
        [AppAction.EraseMutedChannels] = ("Edit", "Erase muted channels"),
        [AppAction.SetSequenceName] = ("Edit", "Set sequence name"),
        [AppAction.DeleteListItem] = ("Edit", "Delete selected list item(s)"),

        [AppAction.SelectPreviousSong] = ("Song list", "Select previous song"),
        [AppAction.SelectNextSong] = ("Song list", "Select next song"),
        [AppAction.LoadSelectedSong] = ("Song list", "Load selected song (plays if Auto)"),
        [AppAction.LoadAndPlaySelectedSong] = ("Song list", "Load and play selected song"),
        [AppAction.PreviousSong] = ("Song list", "Previous song (load and play)"),
        [AppAction.NextSong] = ("Song list", "Next song (load and play)"),
        [AppAction.PreviousPlaylist] = ("Song list", "Previous playlist"),
        [AppAction.NextPlaylist] = ("Song list", "Next playlist"),

        [AppAction.NewPlaylist] = ("Playlists", "New playlist"),
        [AppAction.RenamePlaylist] = ("Playlists", "Rename playlist"),
        [AppAction.DeletePlaylist] = ("Playlists", "Delete playlist"),
        [AppAction.ImportPlaylists] = ("Playlists", "Import playlist(s)"),
        [AppAction.ExportPlaylist] = ("Playlists", "Export playlist"),
        [AppAction.ExportAllPlaylists] = ("Playlists", "Export all playlists"),
        [AppAction.SearchPlaylists] = ("Playlists", "Search in playlists"),
        [AppAction.AddFiles] = ("Playlists", "Add file(s)"),
        [AppAction.AddFolder] = ("Playlists", "Add files from folder(s)"),
        [AppAction.SortList] = ("Playlists", "Sort alphabetically"),
        [AppAction.RandomizeList] = ("Playlists", "Randomize list"),
        [AppAction.FindOnComputer] = ("Playlists", "Find on computer"),

        [AppAction.ShowKeyboardShortcuts] = ("Help", "Keyboard shortcuts"),
        [AppAction.About] = ("Help", "About"),
    };

    public static string GetName(AppAction action) =>
        Info.TryGetValue(action, out var i) ? i.Name : action.ToString();

    public static string GetCategory(AppAction action) =>
        Info.TryGetValue(action, out var i) ? i.Category : "Other";

    /// <summary>Actions that make sense to auto-repeat while a key is held down.</summary>
    public static bool IsRepeatable(AppAction action) => action is
        AppAction.PreviousBar or AppAction.NextBar or
        AppAction.TempoUp or AppAction.TempoDown or
        AppAction.TransposeUp or AppAction.TransposeDown or
        AppAction.SelectPreviousSong or AppAction.SelectNextSong or
        AppAction.PreviousPlaylist or AppAction.NextPlaylist;

    public static IReadOnlyList<AppAction> All { get; } = Enum.GetValues<AppAction>();
}
