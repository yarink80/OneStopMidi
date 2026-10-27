using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OneStopMidi.App.Common;
using OneStopMidi.App.Input;
using OneStopMidi.App.Settings;
using OneStopMidi.Core;

namespace OneStopMidi.App.Views;

public partial class SettingsWindow : Window
{
    public enum Page { Various, MusicDevice, Routing, DrumChannels, Keyboard, Startup }

    public sealed class KeyRow : ObservableObject
    {
        private string _keys = "";
        public KeyRow(AppAction action) => Action = action;
        public AppAction Action { get; }
        public string Category => AppActionInfo.GetCategory(Action);
        public string Name => AppActionInfo.GetName(Action);
        public string Keys { get => _keys; set => Set(ref _keys, value); }
    }

    private sealed record Option<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private static readonly (int CodePage, string Name)[] Charsets =
    [
        (1252, "Western European (Windows-1252)"),
        (1250, "Central European (Windows-1250)"),
        (1251, "Cyrillic (Windows-1251)"),
        (1253, "Greek (Windows-1253)"),
        (1254, "Turkish (Windows-1254)"),
        (1255, "Hebrew (Windows-1255)"),
        (1256, "Arabic (Windows-1256)"),
        (1257, "Baltic (Windows-1257)"),
        (1258, "Vietnamese (Windows-1258)"),
        (874, "Thai (Windows-874)"),
        (932, "Japanese (Shift-JIS)"),
        (936, "Chinese Simplified (GBK)"),
        (949, "Korean (EUC-KR)"),
        (950, "Chinese Traditional (Big5)"),
        (65001, "Unicode (UTF-8)"),
    ];

    private readonly AppSettings _settings;
    private readonly IReadOnlyList<MidiDeviceInfo> _devices;
    private readonly List<KeyRow> _rows;
    private readonly ComboBox[] _portBoxes = new ComboBox[ChannelRouteFormat.MaxPorts];
    private readonly ComboBox[] _routeBoxes = new ComboBox[16];
    private readonly CheckBox[] _drumBoxes = new CheckBox[16];
    private bool _loadingPreset;

    /// <summary>The edited key map (valid after OK).</summary>
    public KeyMap KeyMap { get; }

    /// <summary>The edited settings (valid after OK).</summary>
    public AppSettings Result => _settings;

    public SettingsWindow(AppSettings settings, KeyMap keyMap, IReadOnlyList<MidiDeviceInfo> devices,
        IReadOnlyList<string> playlistNames, Page page = Page.Various)
    {
        InitializeComponent();
        _settings = settings.Clone();
        KeyMap = keyMap.Clone();
        _devices = devices;

        LoadVarious();
        BuildPorts();
        BuildRoutes();
        BuildDrums();

        _rows = AppActionInfo.All.Select(a => new KeyRow(a)).ToList();
        RefreshKeyRows();
        ActionList.ItemsSource = _rows;
        _loadingPreset = true;
        PresetBox.ItemsSource = KeyMap.PresetNames.Concat(["Custom"]).ToList();
        PresetBox.SelectedItem = KeyMap.PresetNames.Contains(_settings.KeyPreset) ? _settings.KeyPreset : "Custom";
        _loadingPreset = false;

        LoadStartup(playlistNames);
        Tabs.SelectedIndex = (int)page;
    }

    // ================================================================== Various

    private void LoadVarious()
    {
        ChaseBox.IsChecked = _settings.ChaseEvents;
        WarningsBox.IsChecked = _settings.ShowMidiWarnings;
        SpacebarBox.IsChecked = _settings.SpacebarTogglesPauseContinue;
        TrackNamesBox.IsChecked = _settings.DisplayTrackNames;
        ClickBox.IsChecked = _settings.PlayClick;

        ChordModeBox.ItemsSource = new[]
        {
            new Option<ChordDisplayMode>(ChordDisplayMode.Off, "Off"),
            new Option<ChordDisplayMode>(ChordDisplayMode.Embedded, "Embedded chords only"),
            new Option<ChordDisplayMode>(ChordDisplayMode.AutoDetect, "Auto-detect chords"),
            new Option<ChordDisplayMode>(ChordDisplayMode.PreferEmbeddedThenDetect, "Embedded, else auto-detect"),
        };
        ChordModeBox.SelectedItem = ((Option<ChordDisplayMode>[])ChordModeBox.ItemsSource).First(o => o.Value == _settings.ChordDisplay);

        var charsets = Charsets.Select(c => new Option<int>(c.CodePage, c.Name)).ToList();
        if (charsets.All(c => c.Value != _settings.CharacterCodePage))
            charsets.Add(new Option<int>(_settings.CharacterCodePage, $"Code page {_settings.CharacterCodePage}"));
        CharsetBox.ItemsSource = charsets;
        CharsetBox.SelectedItem = charsets.First(c => c.Value == _settings.CharacterCodePage);

        var formats = new[]
        {
            new Option<MidiSaveFormat>(MidiSaveFormat.Original, "Keep original format"),
            new Option<MidiSaveFormat>(MidiSaveFormat.Format0, "Standard MIDI File format 0"),
            new Option<MidiSaveFormat>(MidiSaveFormat.Format1, "Standard MIDI File format 1"),
        };
        SaveFormatBox.ItemsSource = formats;
        SaveFormatBox.SelectedItem = formats.First(f => f.Value == _settings.SaveFormat);
    }

    private void SaveVarious()
    {
        _settings.ChaseEvents = ChaseBox.IsChecked == true;
        _settings.ShowMidiWarnings = WarningsBox.IsChecked == true;
        _settings.SpacebarTogglesPauseContinue = SpacebarBox.IsChecked == true;
        _settings.DisplayTrackNames = TrackNamesBox.IsChecked == true;
        _settings.PlayClick = ClickBox.IsChecked == true;
        if (ChordModeBox.SelectedItem is Option<ChordDisplayMode> cm) _settings.ChordDisplay = cm.Value;
        if (CharsetBox.SelectedItem is Option<int> cs) _settings.CharacterCodePage = cs.Value;
        if (SaveFormatBox.SelectedItem is Option<MidiSaveFormat> sf) _settings.SaveFormat = sf.Value;
    }

    // ================================================================== Music device / routing / drums

    private void BuildPorts()
    {
        for (var p = 0; p < ChannelRouteFormat.MaxPorts; p++)
        {
            PortsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = $"Port {(char)('A' + p)}:", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, p);
            PortsGrid.Children.Add(label);

            var items = new List<Option<int>>();
            if (p > 0) items.Add(new Option<int>(int.MinValue, "(not used)"));
            items.AddRange(_devices.Select(d => new Option<int>(d.Id, d.Name)));
            var current = p < _settings.OutputDevices.Count ? _settings.OutputDevices[p] : int.MinValue;
            if (current != int.MinValue && items.All(i => i.Value != current))
                items.Add(new Option<int>(current, $"Device {current} (not available)"));
            var box = new ComboBox { ItemsSource = items, Margin = new Thickness(0, 3, 0, 3) };
            box.SelectedItem = items.FirstOrDefault(i => i.Value == current) ?? items.FirstOrDefault();
            Grid.SetRow(box, p);
            Grid.SetColumn(box, 1);
            PortsGrid.Children.Add(box);
            _portBoxes[p] = box;
        }
    }

    private void BuildRoutes()
    {
        var options = new List<string>();
        for (var p = 0; p < ChannelRouteFormat.MaxPorts; p++)
            for (var c = 1; c <= 16; c++)
                options.Add($"{(char)('A' + p)}{c}");
        // Column-major: channels 1-8 left, 9-16 right.
        var order = Enumerable.Range(0, 8).SelectMany(i => new[] { i, i + 8 });
        foreach (var ch in order)
        {
            var panel = new DockPanel { Margin = new Thickness(0, 2, 16, 2) };
            panel.Children.Add(new TextBlock { Text = $"Channel {ch + 1}  →", Width = 90, VerticalAlignment = VerticalAlignment.Center });
            var box = new ComboBox { ItemsSource = options, Width = 80, HorizontalAlignment = HorizontalAlignment.Left, SelectedItem = _settings.Routes[ch] };
            panel.Children.Add(box);
            RoutesGrid.Children.Add(panel);
            _routeBoxes[ch] = box;
        }
    }

    private void ResetRoutes_Click(object sender, RoutedEventArgs e)
    {
        for (var i = 0; i < 16; i++) _routeBoxes[i].SelectedItem = $"A{i + 1}";
    }

    private void BuildDrums()
    {
        for (var i = 0; i < 16; i++)
        {
            var cb = new CheckBox { Content = $"Channel {i + 1}", IsChecked = _settings.DrumChannels[i] };
            DrumGrid.Children.Add(cb);
            _drumBoxes[i] = cb;
        }
    }

    private void SaveDevices()
    {
        var ports = new List<int>();
        for (var p = 0; p < ChannelRouteFormat.MaxPorts; p++)
        {
            if (_portBoxes[p].SelectedItem is not Option<int> o || o.Value == int.MinValue)
            {
                if (p == 0) ports.Add(_devices.Count > 0 ? _devices[0].Id : 0);
                else break; // ports must be contiguous
                continue;
            }
            ports.Add(o.Value);
        }
        _settings.OutputDevices = ports;
        _settings.Routes = _routeBoxes.Select((b, i) => b.SelectedItem as string ?? $"A{i + 1}").ToList();
        _settings.DrumChannels = _drumBoxes.Select(b => b.IsChecked == true).ToList();
    }

    // ================================================================== Keyboard

    private AppAction? SelectedAction => (ActionList.SelectedItem as KeyRow)?.Action;

    private void RefreshKeyRows()
    {
        foreach (var r in _rows) r.Keys = KeyMap.FormatChords(r.Action);
        RefreshChordList();
    }

    private void RefreshChordList()
    {
        if (SelectedAction is not { } a)
        {
            SelectedActionText.Text = "Select a command";
            ChordList.ItemsSource = null;
            return;
        }
        SelectedActionText.Text = AppActionInfo.GetName(a);
        ChordList.ItemsSource = KeyMap.GetChords(a).ToList();
        UpdateConflictText();
    }

    private void KeyFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var f = KeyFilterBox.Text.Trim();
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(ActionList.ItemsSource);
        view.Filter = f.Length == 0
            ? null
            : o => o is KeyRow r && (r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                     r.Category.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                     r.Keys.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshChordList();

    private void CaptureBox_ChordCaptured(object? sender, KeyChord chord) => UpdateConflictText();

    private void UpdateConflictText()
    {
        ConflictText.Text = "";
        if (CaptureBox.Chord is not { } chord || SelectedAction is not { } action) return;
        if (KeyMap.FindConflict(chord, action) is { } other)
            ConflictText.Text = $"{chord.Format()} is currently assigned to \"{AppActionInfo.GetName(other)}\".";
    }

    private void AddChord_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAction is not { } action)
        {
            MessageBox.Show(this, "Select a command in the list first.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (CaptureBox.Chord is not { } chord)
        {
            CaptureBox.Focus();
            return;
        }
        switch (KeyMap.Add(action, chord))
        {
            case KeyBindResult.Conflict:
                var other = KeyMap.FindConflict(chord, action)!.Value;
                var answer = MessageBox.Show(this,
                    $"{chord.Format()} is already assigned to \"{AppActionInfo.GetName(other)}\".\n\nReassign it to \"{AppActionInfo.GetName(action)}\"?",
                    "Shortcut conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                KeyMap.Reassign(action, chord);
                break;
            case KeyBindResult.Invalid:
                return;
        }
        MarkCustom();
        CaptureBox.Chord = null;
        RefreshKeyRows();
    }

    private void RemoveChord_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAction is not { } action || ChordList.SelectedItem is not KeyChord chord) return;
        KeyMap.Remove(action, chord);
        MarkCustom();
        RefreshKeyRows();
    }

    private void ResetAction_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAction is not { } action) return;
        KeyMap.ResetAction(action);
        MarkCustom();
        RefreshKeyRows();
    }

    private void ResetAllKeys_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset all shortcuts to the defaults?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        KeyMap.ResetToDefaults();
        _loadingPreset = true;
        PresetBox.SelectedItem = KeyMap.DefaultPresetName;
        _loadingPreset = false;
        _settings.KeyPreset = KeyMap.DefaultPresetName;
        RefreshKeyRows();
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPreset || PresetBox.SelectedItem is not string name || name == "Custom") return;
        var answer = MessageBox.Show(this, $"Replace all shortcuts with the \"{name}\" preset?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            _loadingPreset = true;
            PresetBox.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : "Custom";
            _loadingPreset = false;
            return;
        }
        KeyMap.ApplyPreset(name);
        _settings.KeyPreset = name;
        RefreshKeyRows();
    }

    private void MarkCustom()
    {
        _settings.KeyPreset = "Custom";
        _loadingPreset = true;
        PresetBox.SelectedItem = "Custom";
        _loadingPreset = false;
    }

    private void ImportKeys_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Key map (*.json)|*.json|All files (*.*)|*.*", Title = "Import key map" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            KeyMap.CopyFrom(KeyMap.Import(dlg.FileName));
            MarkCustom();
            RefreshKeyRows();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not import the key map:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportKeys_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Key map (*.json)|*.json", FileName = "OneStopMidi keys.json", Title = "Export key map" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            KeyMap.Export(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not export the key map:\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================================================================== Startup

    private void LoadStartup(IReadOnlyList<string> playlistNames)
    {
        var s = _settings.Startup;
        StartNoneRadio.IsChecked = s.Mode == StartupPlaylistMode.None;
        StartLastRadio.IsChecked = s.Mode == StartupPlaylistMode.LastUsedPlaylist;
        StartSpecificRadio.IsChecked = s.Mode == StartupPlaylistMode.SpecificPlaylist;
        SpecificBox.ItemsSource = playlistNames;
        SpecificBox.Text = s.SpecificPlaylist ?? "";
        FirstSongRadio.IsChecked = s.SongMode == StartupSongMode.FirstSong;
        ResumeSongRadio.IsChecked = s.SongMode == StartupSongMode.ResumeLastSong;
        AutoLoadBox.IsChecked = s.AutoLoad;
        AutoPlayBox.IsChecked = s.AutoPlay;
        ForcePlaylistBox.IsChecked = s.ForcePlaylistModeOn;
        ForceAutoBox.IsChecked = s.ForceAutoOn;
        UpdateStartupEnabled();
    }

    private void StartMode_Changed(object sender, RoutedEventArgs e) => UpdateStartupEnabled();
    private void AutoLoad_Changed(object sender, RoutedEventArgs e) => UpdateStartupEnabled();

    private void UpdateStartupEnabled()
    {
        if (SpecificPanel is null || AutoPlayBox is null) return;
        SpecificPanel.IsEnabled = StartSpecificRadio.IsChecked == true;
        AutoPlayBox.IsEnabled = AutoLoadBox.IsChecked == true;
    }

    private void BrowseStartupPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Playlists (*.txt;*.m3u;*.m3u8)|*.txt;*.m3u;*.m3u8|All files (*.*)|*.*",
            Title = "Choose a playlist file",
        };
        if (dlg.ShowDialog(this) == true) SpecificBox.Text = dlg.FileName;
    }

    private bool SaveStartup()
    {
        var s = _settings.Startup;
        s.Mode = StartNoneRadio.IsChecked == true ? StartupPlaylistMode.None
            : StartSpecificRadio.IsChecked == true ? StartupPlaylistMode.SpecificPlaylist
            : StartupPlaylistMode.LastUsedPlaylist;
        s.SpecificPlaylist = string.IsNullOrWhiteSpace(SpecificBox.Text) ? null : SpecificBox.Text.Trim();
        if (s.Mode == StartupPlaylistMode.SpecificPlaylist && s.SpecificPlaylist is null)
        {
            Tabs.SelectedItem = StartupTab;
            MessageBox.Show(this, "Choose the playlist to open at startup.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        s.SongMode = ResumeSongRadio.IsChecked == true ? StartupSongMode.ResumeLastSong : StartupSongMode.FirstSong;
        s.AutoLoad = AutoLoadBox.IsChecked == true;
        s.AutoPlay = AutoPlayBox.IsChecked == true;
        s.ForcePlaylistModeOn = ForcePlaylistBox.IsChecked == true;
        s.ForceAutoOn = ForceAutoBox.IsChecked == true;
        return true;
    }

    // ================================================================== OK

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SaveVarious();
        SaveDevices();
        if (!SaveStartup()) return;
        _settings.KeyBindings = KeyMap.ToDictionary();
        DialogResult = true;
    }

    /// <summary>Encoding for the configured code page (falls back to Windows-1252 / Latin-1).</summary>
    public static Encoding GetEncoding(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch
        {
            try { return Encoding.GetEncoding(1252); } catch { return Encoding.Latin1; }
        }
    }
}
