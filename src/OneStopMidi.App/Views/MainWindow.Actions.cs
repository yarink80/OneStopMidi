using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using OneStopMidi.App.Input;
using OneStopMidi.App.Settings;
using OneStopMidi.Core;

namespace OneStopMidi.App.Views;

public partial class MainWindow
{
    // ================================================================== key dispatch

    /// <summary>
    /// All shortcuts go through here. PreviewKeyDown tunnels from the window first, so arrow keys and Enter are
    /// handled before the ListView/TreeView can consume them. Text boxes, menus (incl. Alt navigation) and open
    /// drop-downs are left alone (see <see cref="KeyInput.ShouldIntercept"/>).
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (!KeyInput.ShouldIntercept(e, Keyboard.FocusedElement as DependencyObject)) return;
        var chord = KeyChord.FromKeyEventArgs(e);
        if (!chord.IsValid || !_keyMap.TryGetAction(chord, out var action)) return;
        e.Handled = true;
        if (e.IsRepeat && !AppActionInfo.IsRepeatable(action)) return;
        ExecuteAction(action);
    }

    private void UpdateMenuGestures()
    {
        foreach (var item in MainMenu.Items.OfType<MenuItem>()) UpdateGestures(item);
        if (PlaylistTree.ContextMenu is { } cm1) foreach (var i in cm1.Items.OfType<MenuItem>()) UpdateGestures(i);
        if (SongList.ContextMenu is { } cm2) foreach (var i in cm2.Items.OfType<MenuItem>()) UpdateGestures(i);
    }

    private void UpdateGestures(MenuItem item)
    {
        if (item.Tag is AppAction a && a != AppAction.ExitApp)
        {
            var chords = _keyMap.GetChords(a);
            item.InputGestureText = string.Join(", ", chords.Take(2).Select(c => c.Format()));
        }
        foreach (var child in item.Items.OfType<MenuItem>()) UpdateGestures(child);
    }

    private void Menu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        var song = _engine.Song;
        ClickMenuItem.IsChecked = _engine.ClickEnabled;
        ChaseMenuItem.IsChecked = _engine.ChaseEvents;
        PlaylistModeMenuItem.IsChecked = _playlistModeOn;
        AutoMenuItem.IsChecked = _autoOn;
        LyricsMenuItem.IsChecked = Karaoke.Visibility == Visibility.Visible;
        LoopMenuItem.IsChecked = _engine.LoopEnabled;
        SaveMenuItem.IsEnabled = song is not null;
        UndoMenuItem.IsEnabled = SafeGet(() => _engine.CanUndo);
        RedoMenuItem.IsEnabled = SafeGet(() => _engine.CanRedo);
    }

    private static bool SafeGet(Func<bool> f)
    {
        try { return f(); } catch { return false; }
    }

    private void ActionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppAction a }) ExecuteAction(a);
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppAction a }) ExecuteAction(a);
    }

    // ================================================================== actions

    public void ExecuteAction(AppAction action)
    {
        try
        {
            ExecuteActionCore(action);
        }
        catch (NotImplementedException)
        {
            SetStatus($"\"{AppActionInfo.GetName(action)}\" is not available in this engine yet.");
        }
        catch (Exception ex)
        {
            Common.CrashLog.Write($"Action {action} failed", ex);
            SetStatus($"{AppActionInfo.GetName(action)}: {ex.Message}");
        }
    }

    private bool ListHasFocus => SongList.IsKeyboardFocusWithin;
    private bool TreeHasFocus => PlaylistTree.IsKeyboardFocusWithin;

    private void ExecuteActionCore(AppAction action)
    {
        var song = _engine.Song;
        switch (action)
        {
            // ---- File
            case AppAction.OpenFile: OpenFile(); break;
            case AppAction.SaveFile: SaveSong(saveAs: false); break;
            case AppAction.SaveFileAs: SaveSong(saveAs: true); break;
            case AppAction.RevertToSaved:
                if (song is null) break;
                if (MessageBox.Show(this, "Discard all changes and reload the file?", "Revert to saved", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _engine.RevertToSaved();
                break;
            case AppAction.OpenSettings: ShowSettings(); break;
            case AppAction.ExitApp: Close(); break;

            // ---- Transport
            case AppAction.Play:
                if (EnsureSongLoaded()) _engine.Play();
                break;
            case AppAction.Stop: _engine.Stop(); break;
            case AppAction.StartStop:
                if (_settings.SpacebarTogglesPauseContinue)
                {
                    if (EnsureSongLoaded()) _engine.PauseContinue();
                }
                else if (_engine.State == PlaybackState.Playing) _engine.Stop();
                else if (_engine.State == PlaybackState.Paused) _engine.PauseContinue();
                else if (EnsureSongLoaded()) _engine.Play();
                break;
            case AppAction.PauseContinue:
                if (EnsureSongLoaded()) _engine.PauseContinue();
                break;
            case AppAction.PreviousBar: _engine.SeekBars(-1); break;
            case AppAction.NextBar: _engine.SeekBars(1); break;
            case AppAction.GoToStart: _engine.Seek(0); break;
            case AppAction.ToggleLoop:
                _engine.LoopEnabled = !_engine.LoopEnabled;
                SetStatus(_engine.LoopEnabled ? "Loop on" : "Loop off");
                break;
            case AppAction.SetLeftLocator:
                if (song is null) break;
                _engine.LeftLocator = _engine.PositionTicks;
                if (_engine.RightLocator < _engine.LeftLocator) _engine.RightLocator = song.LengthTicks;
                break;
            case AppAction.ResetLeftLocator: _engine.LeftLocator = 0; break;
            case AppAction.SetRightLocator:
                if (song is null) break;
                _engine.RightLocator = _engine.PositionTicks;
                if (_engine.LeftLocator > _engine.RightLocator) _engine.LeftLocator = 0;
                break;
            case AppAction.ResetRightLocator:
                if (song is not null) _engine.RightLocator = song.LengthTicks;
                break;
            case AppAction.GoToLeftLocator: _engine.Seek(_engine.LeftLocator); break;

            // ---- Tempo / pitch
            case AppAction.TempoUp: _engine.TempoPercent = Math.Min(200, _engine.TempoPercent + 1); break;
            case AppAction.TempoDown: _engine.TempoPercent = Math.Max(-90, _engine.TempoPercent - 1); break;
            case AppAction.TempoReset:
                _engine.TempoPercent = 0;
                if (_engine.CanSetBaseTempo) _engine.BaseTempoOverride = null;
                break;
            case AppAction.TransposeUp: _engine.Transpose = Math.Min(24, _engine.Transpose + 1); break;
            case AppAction.TransposeDown: _engine.Transpose = Math.Max(-24, _engine.Transpose - 1); break;
            case AppAction.TransposeReset: _engine.Transpose = 0; break;

            // ---- Modes
            case AppAction.ToggleClick:
                _engine.ClickEnabled = !_engine.ClickEnabled;
                _settings.PlayClick = _engine.ClickEnabled;
                SetStatus(_engine.ClickEnabled ? "MIDI click on" : "MIDI click off");
                break;
            case AppAction.ToggleChaseEvents:
                _engine.ChaseEvents = !_engine.ChaseEvents;
                _settings.ChaseEvents = _engine.ChaseEvents;
                SetStatus(_engine.ChaseEvents ? "Chase events on" : "Chase events off");
                break;
            case AppAction.ToggleAutoplay:
                _autoOn = !_autoOn;
                UpdateModeToggles();
                SetStatus(_autoOn ? "Auto on" : "Auto off");
                break;
            case AppAction.TogglePlaylistMode:
                _playlistModeOn = !_playlistModeOn;
                UpdateModeToggles();
                SetStatus(_playlistModeOn ? "Playlist mode on" : "Playlist mode off");
                break;
            case AppAction.ToggleLyrics: SetKaraokeVisible(Karaoke.Visibility != Visibility.Visible); break;

            // ---- Mixer
            case AppAction.FullVolume: _engine.FullVolume(); break;
            case AppAction.ResetDevices: _engine.ResetDevices(); break;

            // ---- Edit (list items when the list has focus, otherwise the song range)
            case AppAction.Undo: _engine.Undo(); break;
            case AppAction.Redo: _engine.Redo(); break;
            case AppAction.Cut:
                if (ListHasFocus) CutFiles();
                else _engine.CutSelection();
                break;
            case AppAction.Copy:
                if (ListHasFocus) CopyFiles();
                else _engine.CopySelection();
                break;
            case AppAction.Paste:
                if (ListHasFocus || TreeHasFocus) PasteFiles();
                else _engine.Paste();
                break;
            case AppAction.DeleteRange: _engine.DeleteSelection(); break;
            case AppAction.EraseMutedChannels: _engine.EraseMutedChannels(); break;
            case AppAction.SetSequenceName:
                if (song is null) break;
                var name = InputDialog.Ask(this, "Sequence name", "Sequence name:", song.SequenceName ?? "");
                if (name is not null) _engine.SetSequenceName(name);
                _shownSong = null;
                break;
            case AppAction.DeleteListItem:
                if (TreeHasFocus) DeleteCurrentPlaylist();
                else DeleteFiles();
                break;

            // ---- Song list navigation
            case AppAction.SelectPreviousSong: MoveSelection(-1); break;
            case AppAction.SelectNextSong: MoveSelection(1); break;
            case AppAction.LoadSelectedSong: LoadSelected(play: _autoOn); break;
            case AppAction.LoadAndPlaySelectedSong: LoadSelected(play: true); break;
            case AppAction.PreviousSong: StepSong(-1); break;
            case AppAction.NextSong: StepSong(1); break;
            case AppAction.PreviousPlaylist: StepPlaylist(-1); break;
            case AppAction.NextPlaylist: StepPlaylist(1); break;

            // ---- Playlists
            case AppAction.NewPlaylist: NewPlaylist(); break;
            case AppAction.RenamePlaylist: RenameCurrentPlaylist(); break;
            case AppAction.DeletePlaylist: DeleteCurrentPlaylist(); break;
            case AppAction.ImportPlaylists: ImportPlaylists(legacyOnly: false); break;
            case AppAction.ExportPlaylist: ExportCurrentPlaylist(); break;
            case AppAction.ExportAllPlaylists: ExportAllPlaylists(); break;
            case AppAction.SearchPlaylists: SearchPlaylists(); break;
            case AppAction.AddFiles: AddFilesDialog(); break;
            case AppAction.AddFolder: AddFolderDialog(); break;
            case AppAction.SortList: SortCurrentList(); break;
            case AppAction.RandomizeList: RandomizeCurrentList(); break;
            case AppAction.FindOnComputer: FindOnComputer(); break;

            // ---- Help
            case AppAction.ShowKeyboardShortcuts: new ShortcutsWindow(_keyMap) { Owner = this }.ShowDialog(); break;
            case AppAction.About: ShowAbout(); break;
        }
    }

    private void ShowAbout() =>
        new AboutWindow(Engine.EngineFactory.IsSimulated(_engine) ? "simulated (no sound)" : "WinMM MIDI output") { Owner = this }.ShowDialog();

    private void AvatarBadge_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ShowAbout();

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await Common.Updater.CheckAsync(this);

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => Common.Updater.OpenReleasesPage();

    // ================================================================== modes

    private void UpdateModeToggles()
    {
        PlaylistToggle.IsChecked = _playlistModeOn;
        AutoToggle.IsChecked = _autoOn;
        _settings.PlaylistModeOn = _playlistModeOn;
        _settings.AutoplayOn = _autoOn;
    }

    private void PlaylistToggle_Click(object sender, RoutedEventArgs e) => ExecuteAction(AppAction.TogglePlaylistMode);
    private void AutoToggle_Click(object sender, RoutedEventArgs e) => ExecuteAction(AppAction.ToggleAutoplay);
    private void LoopToggle_Click(object sender, RoutedEventArgs e) => ExecuteAction(AppAction.ToggleLoop);
    private void KarToggle_Click(object sender, RoutedEventArgs e) => ExecuteAction(AppAction.ToggleLyrics);

    private void SetKaraokeVisible(bool visible)
    {
        Karaoke.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MixerScroll.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        KarToggle.IsChecked = visible;
        _settings.KaraokeScreenOn = visible;
        if (visible) Karaoke.Update(_engine.Song, _engine.PositionTicks);
    }

    // ================================================================== file open / save

    private const string SongFilter = "MIDI files (*.mid;*.midi;*.kar;*.smf)|*.mid;*.midi;*.kar;*.smf|All files (*.*)|*.*";

    private void OpenFile()
    {
        var dlg = new OpenFileDialog { Filter = SongFilter, Title = "Open MIDI file" };
        if (dlg.ShowDialog(this) != true) return;
        LoadSong(dlg.FileName, play: _autoOn, fromList: null);
    }

    private void SaveSong(bool saveAs)
    {
        var song = _engine.Song;
        if (song?.FilePath is null) return;
        var path = song.FilePath;
        if (saveAs)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "MIDI file (*.mid)|*.mid|Karaoke file (*.kar)|*.kar",
                FileName = Path.GetFileName(path),
                InitialDirectory = Path.GetDirectoryName(path),
                Title = "Save MIDI file as",
            };
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        int? format = _settings.SaveFormat switch
        {
            MidiSaveFormat.Format0 => 0,
            MidiSaveFormat.Format1 => 1,
            _ => null,
        };
        if (TryEngine(() => _engine.Save(path, format), "Save")) SetStatus($"Saved {Path.GetFileName(path)}");
    }

    // ================================================================== cue slider

    private void CueSlider_DragStarted(object sender, DragStartedEventArgs e) => _cueDragging = true;

    private void CueSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _cueDragging = false;
        TryEngine(() => _engine.Seek((long)CueSlider.Value), "Seek");
    }

    private void CueSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Click-to-position (IsMoveToPointEnabled) without a thumb drag.
        if (_cueDragging) return;
        Dispatcher.BeginInvoke(() => TryEngine(() => _engine.Seek((long)CueSlider.Value), "Seek"));
    }

    // ================================================================== editable fields

    private bool TryParsePosition(string text, out long tick)
    {
        tick = 0;
        var song = _engine.Song;
        if (song is null) return false;
        var parts = text.Split([' ', ':', '.', '|'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var nums = new int[3] { 1, 1, 0 };
        for (var i = 0; i < Math.Min(3, parts.Length); i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out nums[i])) return false;
        tick = Math.Clamp(song.FromMusical(new MusicalPosition(Math.Max(1, nums[0]), Math.Max(1, nums[1]), Math.Max(0, nums[2]))), 0, song.LengthTicks);
        return true;
    }

    private void FinishEdit()
    {
        // Return focus to the song list so shortcuts work again.
        SongList.Focus();
    }

    private void PositionBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { FinishEdit(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (TryParsePosition(PositionBox.Text, out var tick)) TryEngine(() => _engine.Seek(tick), "Seek");
        FinishEdit();
    }

    private void LocatorBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { FinishEdit(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitLocator((TextBox)sender);
        FinishEdit();
    }

    private void LocatorBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitLocator((TextBox)sender);

    private void CommitLocator(TextBox box)
    {
        if (!TryParsePosition(box.Text, out var tick)) return;
        if (box == LeftLocBox) TryEngine(() => _engine.LeftLocator = tick, "Left locator");
        else TryEngine(() => _engine.RightLocator = tick, "Right locator");
    }

    private void TempoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { FinishEdit(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_engine.CanSetBaseTempo)
        {
            var t = TempoBox.Text.Trim().Replace(',', '.');
            if (t.Length == 0) TryEngine(() => _engine.BaseTempoOverride = null, "Tempo");
            else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm) && bpm is > 0 and < 1000)
                TryEngine(() => _engine.BaseTempoOverride = bpm, "Tempo");
        }
        FinishEdit();
    }

    private void TempoPercentBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { FinishEdit(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse(TempoPercentBox.Text.Trim().TrimEnd('%'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
            TryEngine(() => _engine.TempoPercent = Math.Clamp(v, -90, 200), "Tempo change");
        FinishEdit();
    }

    private void TransposeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { FinishEdit(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (int.TryParse(TransposeBox.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
            TryEngine(() => _engine.Transpose = Math.Clamp(v, -24, 24), "Transpose");
        FinishEdit();
    }
}
