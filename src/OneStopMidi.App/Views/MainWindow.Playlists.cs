using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OneStopMidi.App.Playlists;
using OneStopMidi.App.Startup;
using OneStopMidi.App.ViewModels;
using OneStopMidi.Core;

namespace OneStopMidi.App.Views;

public partial class MainWindow
{
    private readonly List<PlaylistRef> _tempLists = new();
    private readonly HashSet<string> _failedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fileClipboard = new();
    private PlaylistRef? _currentList;
    private string? _loadedPath;
    private PlaylistRef? _loadedFromList;
    private bool _suppressTreeSelection;

    // ================================================================== tree

    private void BuildTree(PlaylistRef? select = null)
    {
        select ??= _currentList;
        _suppressTreeSelection = true;
        try
        {
            PlaylistTree.Items.Clear();
            var recent = MakeNode(new PlaylistRef(PlaylistKind.Recent, PlaylistStore.RecentlyPlayedName), "");
            PlaylistTree.Items.Add(recent);

            var root = new TreeViewItem
            {
                Header = MakeHeader("Playlists", "", bold: true),
                IsExpanded = true,
                Foreground = (Brush)FindResource("TextBrush"),
            };
            foreach (var t in _tempLists) root.Items.Add(MakeNode(t, "", italic: true));
            foreach (var name in _store.GetPlaylistNames()) root.Items.Add(MakeNode(new PlaylistRef(PlaylistKind.Stored, name), ""));
            PlaylistTree.Items.Add(root);
        }
        finally
        {
            _suppressTreeSelection = false;
        }
        if (select is not null) SelectTreeNode(select, showList: false);
    }

    private TreeViewItem MakeNode(PlaylistRef r, string glyph, bool italic = false) => new()
    {
        Header = MakeHeader(r.Name, glyph, italic: italic),
        Tag = r,
        Foreground = (Brush)FindResource("TextBrush"),
        ToolTip = r.Kind == PlaylistKind.Temporary ? "Temporary list (not saved). Right-click → Save as playlist." : null,
        AllowDrop = true,
    };

    private static StackPanel MakeHeader(string text, string glyph, bool bold = false, bool italic = false)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        p.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
        });
        p.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        });
        return p;
    }

    private IEnumerable<TreeViewItem> AllListNodes()
    {
        foreach (var top in PlaylistTree.Items.OfType<TreeViewItem>())
        {
            if (top.Tag is PlaylistRef) yield return top;
            foreach (var child in top.Items.OfType<TreeViewItem>())
                if (child.Tag is PlaylistRef) yield return child;
        }
    }

    private bool SelectTreeNode(PlaylistRef target, bool showList = true)
    {
        var node = AllListNodes().FirstOrDefault(n => ((PlaylistRef)n.Tag).SameAs(target));
        if (node is null) return false;
        _suppressTreeSelection = true;
        try
        {
            node.IsSelected = true;
            node.BringIntoView();
        }
        finally
        {
            _suppressTreeSelection = false;
        }
        var r = (PlaylistRef)node.Tag;
        if (showList) ShowList(r);
        else _currentList = r;
        return true;
    }

    private void PlaylistTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection) return;
        if (PlaylistTree.SelectedItem is TreeViewItem { Tag: PlaylistRef r }) ShowList(r);
    }

    private void PlaylistTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click selects the node under the mouse so the context menu acts on it.
        var d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        if (d is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void PlaylistContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var kind = _currentList?.Kind;
        RenamePlaylistMenuItem.IsEnabled = kind == PlaylistKind.Stored;
        DeletePlaylistMenuItem.IsEnabled = kind is not null;
        DeletePlaylistMenuItem.Header = kind == PlaylistKind.Recent ? "Clear Recently Played" : "Delete";
        SaveTempMenuItem.Visibility = kind == PlaylistKind.Temporary ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== list

    private List<string> EntriesOf(PlaylistRef r) => r.Kind switch
    {
        PlaylistKind.Recent => _store.GetRecent().ToList(),
        PlaylistKind.Stored => _store.GetEntries(r.Name).ToList(),
        _ => r.TempEntries.ToList(),
    };

    private void ShowList(PlaylistRef r, string? selectPath = null)
    {
        _currentList = r;
        if (r.Kind == PlaylistKind.Stored) _settings.LastPlaylist = r.Name;
        _items.Clear();
        foreach (var p in EntriesOf(r))
            _items.Add(new FileItem(p)
            {
                Failed = _failedPaths.Contains(p),
                IsCurrent = string.Equals(p, _loadedPath, StringComparison.OrdinalIgnoreCase),
            });
        ListHeaderText.Text = r.Name.ToUpperInvariant();
        ListCountText.Text = _items.Count == 1 ? "1 song" : $"{_items.Count} songs";
        var sel = selectPath is not null ? _items.FirstOrDefault(i => i.Path.Equals(selectPath, StringComparison.OrdinalIgnoreCase))
            : _items.FirstOrDefault(i => i.IsCurrent) ?? _items.FirstOrDefault();
        if (sel is not null) SelectItem(sel, focus: false);
    }

    private void ReloadCurrentList(string? selectPath = null)
    {
        if (_currentList is null) return;
        selectPath ??= (SongList.SelectedItem as FileItem)?.Path;
        ShowList(_currentList, selectPath);
    }

    /// <summary>Writes the visible list back to its storage.</summary>
    private void SaveCurrentList(IEnumerable<string> entries)
    {
        if (_currentList is null) return;
        var list = entries.ToList();
        switch (_currentList.Kind)
        {
            case PlaylistKind.Recent: _store.SaveRecent(list); break;
            case PlaylistKind.Stored: _store.SaveEntries(_currentList.Name, list); break;
            default:
                _currentList.TempEntries.Clear();
                _currentList.TempEntries.AddRange(list);
                break;
        }
    }

    private void SelectItem(FileItem item, bool focus)
    {
        SongList.SelectedItem = item;
        SongList.ScrollIntoView(item);
        if (focus || SongList.IsKeyboardFocusWithin)
        {
            SongList.UpdateLayout();
            if (SongList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c) c.Focus();
        }
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        var idx = SongList.SelectedIndex;
        idx = idx < 0 ? (delta > 0 ? 0 : _items.Count - 1) : Math.Clamp(idx + delta, 0, _items.Count - 1);
        SelectItem(_items[idx], focus: false);
    }

    private void StepPlaylist(int delta)
    {
        var nodes = AllListNodes().ToList();
        if (nodes.Count == 0) return;
        var idx = nodes.FindIndex(n => ((PlaylistRef)n.Tag).SameAs(_currentList));
        idx = idx < 0 ? 0 : Math.Clamp(idx + delta, 0, nodes.Count - 1);
        nodes[idx].IsSelected = true;
        nodes[idx].BringIntoView();
    }

    private void SongList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(SongList, d) is ListViewItem)
            LoadSelected(play: _autoOn);
    }

    private void SongContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        PasteFilesMenuItem.IsEnabled = _currentList?.IsEditable == true && (_fileClipboard.Count > 0 || SafeClipboardHasFiles());
    }

    // ================================================================== loading / playlist mode

    private bool LoadSelected(bool play)
    {
        if (SongList.SelectedItem is not FileItem item) return false;
        return LoadSong(item.Path, play, _currentList);
    }

    /// <summary>Loads a song; marks it red in the list on failure. Returns true on success.</summary>
    private bool LoadSong(string path, bool play, PlaylistRef? fromList)
    {
        var options = new SongLoadOptions
        {
            TextEncoding = SettingsWindow.GetEncoding(_settings.CharacterCodePage),
            ChordMode = _settings.ChordDisplay,
        };
        try
        {
            if (_engine.State != PlaybackState.Stopped) _engine.Stop();
            _engine.Load(path, options);
        }
        catch (Exception ex)
        {
            _failedPaths.Add(path);
            foreach (var i in _items.Where(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                i.Missing = !File.Exists(path);
                i.Failed = true;
                i.RefreshTooltip();
            }
            SetStatus($"Could not load {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }

        _failedPaths.Remove(path);
        _loadedPath = path;
        _loadedFromList = fromList ?? _currentList;
        foreach (var i in _items)
        {
            var isThis = i.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
            i.IsCurrent = isThis;
            if (isThis)
            {
                i.Failed = false;
                i.Missing = false;
                i.RefreshTooltip();
            }
        }
        _settings.LastSongPath = path;
        if (_loadedFromList?.Kind == PlaylistKind.Stored) _settings.LastPlaylist = _loadedFromList.Name;
        try
        {
            _store.AddToRecent(path);
        }
        catch (Exception ex)
        {
            Common.CrashLog.Write("Updating Recently Played failed", ex);
        }
        if (_currentList?.Kind == PlaylistKind.Recent) ShowList(_currentList, path);

        _shownSong = null;
        _channelsDirty = true;
        SetStatus($"Loaded {Path.GetFileName(path)}");
        if (play) TryEngine(() => _engine.Play(), "Play");
        return true;
    }

    private bool EnsureSongLoaded()
    {
        if (_engine.Song is not null) return true;
        return LoadSelected(play: false);
    }

    /// <summary>Previous/next song relative to the loaded song (or the selection); loads and plays it.</summary>
    private void StepSong(int delta)
    {
        if (_items.Count == 0) return;
        var idx = IndexOfPath(_items.Select(i => i.Path).ToList(), _loadedPath);
        if (idx < 0) idx = SongList.SelectedIndex;
        var target = idx < 0 ? (delta > 0 ? 0 : _items.Count - 1) : idx + delta;
        for (; target >= 0 && target < _items.Count; target += delta)
        {
            var item = _items[target];
            if (item.Missing) continue;
            SelectItem(item, focus: false);
            if (LoadSong(item.Path, play: true, _currentList)) return;
        }
        SetStatus(delta > 0 ? "End of list." : "Start of list.");
    }

    private static int IndexOfPath(IReadOnlyList<string> list, string? path)
    {
        if (path is null) return -1;
        for (var i = 0; i < list.Count; i++)
            if (list[i].Equals(path, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Playlist mode: when a song ends, load the selected song (if a different one was picked) or the next one.</summary>
    private void OnSongEnded()
    {
        UpdateTransportButtons();
        if (!_playlistModeOn) return;
        var list = _loadedFromList ?? _currentList;
        if (list is null) return;
        var entries = list.SameAs(_currentList) ? _items.Select(i => i.Path).ToList() : EntriesOf(list);
        if (entries.Count == 0) return;

        var showing = list.SameAs(_currentList);
        string? candidate = null;
        if (showing && SongList.SelectedItem is FileItem sel && !sel.Path.Equals(_loadedPath, StringComparison.OrdinalIgnoreCase))
            candidate = sel.Path;

        var start = IndexOfPath(entries, _loadedPath);
        var order = new List<string>();
        if (candidate is not null) order.Add(candidate);
        for (var i = start + 1; i < entries.Count; i++) order.Add(entries[i]);

        foreach (var path in order.Take(50))
        {
            if (!File.Exists(path))
            {
                _failedPaths.Add(path);
                continue;
            }
            if (showing && _items.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { } item)
                SelectItem(item, focus: false);
            if (LoadSong(path, play: _autoOn, list)) return;
        }
        SetStatus("Playlist finished.");
    }

    // ================================================================== startup plan

    private void ApplyStartupPlan(StartupPlan plan)
    {
        _playlistModeOn = plan.PlaylistModeOn;
        _autoOn = plan.AutoOn;
        UpdateModeToggles();

        PlaylistRef? target = null;
        switch (plan.Kind)
        {
            case StartupPlaylistKind.Stored when plan.PlaylistName is not null:
                target = new PlaylistRef(PlaylistKind.Stored, plan.PlaylistName);
                break;
            case StartupPlaylistKind.ExternalFile:
            case StartupPlaylistKind.CommandLineFiles:
                target = AddTempList(plan.PlaylistName ?? StartupPlanner.CommandLineListName, plan.Entries);
                break;
        }

        if (target is not null)
        {
            BuildTree(target);
            ShowList(_currentList ?? target, plan.SongToSelect);
        }
        else
        {
            BuildTree();
        }

        if (plan.LoadSong && plan.SongToSelect is not null)
            LoadSong(plan.SongToSelect, plan.PlaySong, _currentList);

        if (plan.Warnings.Count > 0) SetStatus(string.Join("  ", plan.Warnings));
    }

    private PlaylistRef AddTempList(string name, IEnumerable<string> entries)
    {
        var existing = _tempLists.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _tempLists.Remove(existing);
        var r = new PlaylistRef(PlaylistKind.Temporary, name, entries.ToList());
        _tempLists.Insert(0, r);
        return r;
    }

    // ================================================================== playlist commands

    private void NewPlaylist()
    {
        var name = InputDialog.Ask(this, "New playlist", "Name of the new playlist:", "New playlist");
        if (name is null) return;
        var created = _store.Create(name);
        var r = new PlaylistRef(PlaylistKind.Stored, created);
        BuildTree(r);
        SelectTreeNode(r);
    }

    private void RenameCurrentPlaylist()
    {
        if (_currentList is not { Kind: PlaylistKind.Stored } r)
        {
            if (_currentList?.Kind == PlaylistKind.Temporary) SaveTempAsPlaylist();
            return;
        }
        var name = InputDialog.Ask(this, "Edit name", "New name of the playlist:", r.Name);
        if (name is null || name == r.Name) return;
        try
        {
            var newName = _store.Rename(r.Name, name);
            if (string.Equals(_settings.LastPlaylist, r.Name, StringComparison.OrdinalIgnoreCase)) _settings.LastPlaylist = newName;
            if (_loadedFromList?.SameAs(r) == true) _loadedFromList = new PlaylistRef(PlaylistKind.Stored, newName);
            var nr = new PlaylistRef(PlaylistKind.Stored, newName);
            _currentList = nr;
            BuildTree(nr);
            ShowList(nr);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not rename the playlist:\n{ex.Message}", "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteCurrentPlaylist()
    {
        if (_currentList is not { } r) return;
        switch (r.Kind)
        {
            case PlaylistKind.Recent:
                if (MessageBox.Show(this, "Clear the Recently Played list?", "OneStopMidi", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _store.ClearRecent();
                ShowList(r);
                return;
            case PlaylistKind.Temporary:
                _tempLists.RemoveAll(t => t.SameAs(r));
                break;
            default:
                if (MessageBox.Show(this, $"Delete the playlist \"{r.Name}\"?\n\n(The song files themselves are not deleted.)", "Delete playlist",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                _store.Delete(r.Name);
                break;
        }
        _currentList = null;
        _items.Clear();
        ListHeaderText.Text = "SONGS";
        ListCountText.Text = "";
        BuildTree();
    }

    private void SaveTempAsPlaylist_Click(object sender, RoutedEventArgs e) => SaveTempAsPlaylist();

    private void SaveTempAsPlaylist()
    {
        if (_currentList is not { Kind: PlaylistKind.Temporary } t) return;
        var name = InputDialog.Ask(this, "Save as playlist", "Name of the new playlist:", t.Name);
        if (name is null) return;
        var created = _store.Create(name, t.TempEntries);
        _tempLists.Remove(t);
        var r = new PlaylistRef(PlaylistKind.Stored, created);
        if (_loadedFromList?.SameAs(t) == true) _loadedFromList = r;
        BuildTree(r);
        SelectTreeNode(r);
    }

    private void MovePlaylistUp_Click(object sender, RoutedEventArgs e) => MoveCurrentPlaylist(-1);
    private void MovePlaylistDown_Click(object sender, RoutedEventArgs e) => MoveCurrentPlaylist(1);

    private void MoveCurrentPlaylist(int delta)
    {
        if (_currentList is not { Kind: PlaylistKind.Stored } r) return;
        var names = _store.GetPlaylistNames().ToList();
        var idx = names.FindIndex(n => n.Equals(r.Name, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _store.Move(r.Name, idx + delta);
        BuildTree(r);
    }

    private void ImportOldPlaylists_Click(object sender, RoutedEventArgs e) => ImportPlaylists(legacyOnly: true);

    private void ImportPlaylists(bool legacyOnly)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = legacyOnly ? "Import old playlist type" : "Import playlist(s)",
            Filter = legacyOnly
                ? "Old playlists (*.jpl)|*.jpl|All files (*.*)|*.*"
                : "Playlists (*.txt;*.m3u;*.m3u8;*.jpl)|*.txt;*.m3u;*.m3u8;*.jpl|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        ImportPlaylistFiles(dlg.FileNames);
    }

    private void ImportPlaylistFiles(IEnumerable<string> files)
    {
        string? last = null;
        var count = 0;
        foreach (var f in files)
        {
            try
            {
                last = _store.Import(f);
                count++;
            }
            catch (Exception ex)
            {
                SetStatus($"Could not import {Path.GetFileName(f)}: {ex.Message}");
            }
        }
        if (last is null) return;
        var r = new PlaylistRef(PlaylistKind.Stored, last);
        BuildTree(r);
        SelectTreeNode(r);
        SetStatus($"{count} playlist(s) imported.");
    }

    private void ExportCurrentPlaylist()
    {
        if (_currentList is not { } r) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export playlist",
            FileName = r.Name,
            Filter = "Text playlist (*.txt)|*.txt|M3U playlist (*.m3u)|*.m3u|M3U8 playlist (UTF-8) (*.m3u8)|*.m3u8",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            PlaylistStore.WritePlaylistFile(dlg.FileName, EntriesOf(r));
            SetStatus($"Exported \"{r.Name}\" to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportAllPlaylists()
    {
        var dlg = new OpenFolderDialog { Title = "Export all playlists to folder" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var n = _store.ExportAll(dlg.FolderName);
            SetStatus($"Exported {n} playlist(s) to {dlg.FolderName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SearchPlaylists()
    {
        var initial = (SongList.SelectedItem as FileItem) is { } f ? Path.GetFileNameWithoutExtension(f.Path) : "";
        var dlg = new SearchWindow(_store, initial) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Selected is not { } hit) return;
        var r = hit.Playlist == PlaylistStore.RecentlyPlayedName
            ? new PlaylistRef(PlaylistKind.Recent, hit.Playlist)
            : new PlaylistRef(PlaylistKind.Stored, hit.Playlist);
        SelectTreeNode(r);
        ShowList(_currentList ?? r, hit.Path);
        SongList.Focus();
    }

    // ================================================================== list content commands

    private bool EnsureEditableList()
    {
        if (_currentList is { IsEditable: true }) return true;
        var answer = MessageBox.Show(this, "Songs can only be added to a playlist. Create a new playlist now?", "OneStopMidi",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        NewPlaylist();
        return _currentList is { IsEditable: true };
    }

    private void AddFilesDialog()
    {
        if (!EnsureEditableList()) return;
        var dlg = new OpenFileDialog { Filter = SongFilter, Multiselect = true, Title = "Add file(s)" };
        if (dlg.ShowDialog(this) != true) return;
        AddToCurrentList(dlg.FileNames);
    }

    private void AddFolderDialog()
    {
        if (!EnsureEditableList()) return;
        var dlg = new OpenFolderDialog { Multiselect = true, Title = "Add files from folder(s)" };
        if (dlg.ShowDialog(this) != true) return;
        var files = PlaylistStore.ExpandSongPaths(dlg.FolderNames);
        if (files.Count == 0)
        {
            SetStatus("No MIDI files found in the selected folder(s).");
            return;
        }
        AddToCurrentList(files);
    }

    /// <summary>Inserts paths after the selection (or at the end) of the current editable list.</summary>
    private void AddToCurrentList(IReadOnlyList<string> paths, int? insertAt = null)
    {
        if (_currentList is not { IsEditable: true } || paths.Count == 0) return;
        var entries = _items.Select(i => i.Path).ToList();
        var at = insertAt ?? (SongList.SelectedItems.Count > 0 ? SongList.Items.IndexOf(SongList.SelectedItems[^1]) + 1 : entries.Count);
        entries.InsertRange(Math.Clamp(at, 0, entries.Count), paths);
        SaveCurrentList(entries);
        ReloadCurrentList(paths[0]);
        SetStatus($"{paths.Count} file(s) added to \"{_currentList.Name}\".");
    }

    private List<FileItem> SelectedFileItems() =>
        SongList.SelectedItems.OfType<FileItem>().OrderBy(i => _items.IndexOf(i)).ToList();

    private void CutFiles_Click(object sender, RoutedEventArgs e) => CutFiles();
    private void CopyFiles_Click(object sender, RoutedEventArgs e) => CopyFiles();
    private void PasteFiles_Click(object sender, RoutedEventArgs e) => PasteFiles();
    private void DeleteFiles_Click(object sender, RoutedEventArgs e) => DeleteFiles();

    private void CopyFiles()
    {
        var sel = SelectedFileItems();
        if (sel.Count == 0) return;
        _fileClipboard.Clear();
        _fileClipboard.AddRange(sel.Select(i => i.Path));
        try
        {
            var sc = new StringCollection();
            sc.AddRange(_fileClipboard.Where(File.Exists).ToArray());
            if (sc.Count > 0) Clipboard.SetFileDropList(sc);
        }
        catch
        {
            // the system clipboard may be busy; the internal clipboard still works
        }
        SetStatus($"{sel.Count} file(s) copied.");
    }

    private void CutFiles()
    {
        if (_currentList is null) return;
        CopyFiles();
        DeleteFiles(confirm: false);
    }

    private void PasteFiles()
    {
        var paths = _fileClipboard.ToList();
        if (paths.Count == 0)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                    paths = PlaylistStore.ExpandSongPaths(Clipboard.GetFileDropList().Cast<string>());
            }
            catch
            {
                // ignore clipboard errors
            }
        }
        if (paths.Count == 0) return;
        if (!EnsureEditableList()) return;
        AddToCurrentList(paths);
    }

    private static bool SafeClipboardHasFiles()
    {
        try { return Clipboard.ContainsFileDropList(); } catch { return false; }
    }

    private void DeleteFiles(bool confirm = false)
    {
        if (_currentList is null) return;
        var sel = SelectedFileItems();
        if (sel.Count == 0) return;
        if (confirm && MessageBox.Show(this, $"Remove {sel.Count} file(s) from the list?", "OneStopMidi", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;
        var firstIndex = _items.IndexOf(sel[0]);
        var remove = new HashSet<FileItem>(sel);
        var entries = _items.Where(i => !remove.Contains(i)).Select(i => i.Path).ToList();
        SaveCurrentList(entries);
        var next = entries.Count == 0 ? null : entries[Math.Min(firstIndex, entries.Count - 1)];
        ShowList(_currentList, next);
        if (next is null) SongList.SelectedItem = null;
    }

    private void SortCurrentList()
    {
        if (_currentList is not { IsEditable: true }) return;
        SaveCurrentList(PlaylistStore.SortEntries(_items.Select(i => i.Path)));
        ReloadCurrentList();
    }

    private void RandomizeCurrentList()
    {
        if (_currentList is not { IsEditable: true }) return;
        SaveCurrentList(PlaylistStore.Shuffle(_items.Select(i => i.Path)));
        ReloadCurrentList();
    }

    private void FindOnComputer()
    {
        var path = (SongList.SelectedItem as FileItem)?.Path ?? _loadedPath;
        if (path is null) return;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
            else SetStatus("The file and its folder no longer exist.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not open Explorer: " + ex.Message);
        }
    }

    // ================================================================== drag & drop

    private static string[]? DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private static void SetCopyEffect(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e) => SetCopyEffect(e);
    private void SongList_DragOver(object sender, DragEventArgs e) => SetCopyEffect(e);
    private void PlaylistTree_DragOver(object sender, DragEventArgs e) => SetCopyEffect(e);

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (DroppedPaths(e) is not { } paths) return;
        e.Handled = true;
        if (_dragFromList) return; // songs dragged out of the list and dropped back on the window
        HandleDrop(paths, targetList: null);
    }

    private void SongList_Drop(object sender, DragEventArgs e)
    {
        if (DroppedPaths(e) is not { } paths) return;
        e.Handled = true;
        if (_dragFromList) return; // dropped onto itself
        HandleDrop(paths, targetList: _currentList);
    }

    private void PlaylistTree_Drop(object sender, DragEventArgs e)
    {
        if (DroppedPaths(e) is not { } paths) return;
        e.Handled = true;
        var d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not TreeViewItem) d = VisualTreeHelper.GetParent(d);
        var target = (d as TreeViewItem)?.Tag as PlaylistRef;
        if (target is { Kind: PlaylistKind.Stored })
        {
            var songs = PlaylistStore.ExpandSongPaths(paths);
            if (songs.Count == 0) return;
            _store.AddFiles(target.Name, songs);
            if (target.SameAs(_currentList)) ReloadCurrentList();
            SetStatus($"{songs.Count} file(s) added to \"{target.Name}\".");
            return;
        }
        HandleDrop(paths, targetList: null, createListWhenNeeded: true);
    }

    /// <summary>
    /// Dropped playlists are imported; dropped songs/folders go into the target (or current) editable list,
    /// otherwise into a new temporary list, and the first one is loaded.
    /// </summary>
    private void HandleDrop(string[] paths, PlaylistRef? targetList, bool createListWhenNeeded = false)
    {
        var playlistFiles = paths.Where(p => File.Exists(p) && PlaylistStore.IsPlaylistFile(p) && !PlaylistStore.HasSongExtension(p)).ToList();
        if (playlistFiles.Count > 0) ImportPlaylistFiles(playlistFiles);

        var songs = PlaylistStore.ExpandSongPaths(paths.Except(playlistFiles));
        if (songs.Count == 0) return;

        var list = targetList ?? _currentList;
        if (list is { IsEditable: true } && !createListWhenNeeded)
        {
            if (!list.SameAs(_currentList)) SelectTreeNode(list);
            AddToCurrentList(songs);
            return;
        }
        var temp = AddTempList("Dropped files", songs);
        BuildTree(temp);
        SelectTreeNode(temp);
        ShowList(temp, songs[0]);
        LoadSong(songs[0], play: _autoOn, temp);
    }

    // Drag songs out of the list (e.g. onto a playlist in the tree).
    private Point _dragStart;
    private bool _dragArmed;
    private bool _dragFromList;

    private void SongList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(SongList);
        _dragArmed = e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(SongList, d) is ListViewItem { IsSelected: true };
    }

    private void SongList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(SongList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var paths = SelectedFileItems().Select(i => i.Path).ToArray();
        if (paths.Length == 0) return;
        _dragFromList = true;
        try
        {
            DragDrop.DoDragDrop(SongList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
        }
        finally
        {
            _dragFromList = false;
        }
    }
}
