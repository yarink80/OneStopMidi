using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using OneStopMidi.App.Common;
using OneStopMidi.App.Engine;
using OneStopMidi.App.Input;
using OneStopMidi.App.Playlists;
using OneStopMidi.App.Settings;
using OneStopMidi.App.Startup;
using OneStopMidi.App.ViewModels;
using OneStopMidi.Core;

namespace OneStopMidi.App.Views;

public partial class MainWindow : Window
{
    private readonly IPlayerEngine _engine;
    private readonly SettingsStore _settingsStore;
    private readonly PlaylistStore _store;
    private readonly StartupPlan _startupPlan;
    private readonly MixerModel _mixer;
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<FileItem> _items = new();
    private AppSettings _settings;
    private KeyMap _keyMap;

    private volatile bool _channelsDirty = true;
    private IMidiSong? _shownSong;
    private bool _cueDragging;
    private bool _playlistModeOn;
    private bool _autoOn;

    public MainWindow(IPlayerEngine engine, AppSettings settings, SettingsStore settingsStore, PlaylistStore store, StartupPlan startupPlan)
    {
        _engine = engine;
        _settings = settings;
        _settingsStore = settingsStore;
        _store = store;
        _startupPlan = startupPlan;
        _keyMap = KeyMap.FromDictionary(settings.KeyBindings);
        if (settings.KeyBindings is null && settings.KeyPreset != KeyMap.DefaultPresetName)
            _keyMap = KeyMap.CreatePreset(settings.KeyPreset);

        InitializeComponent();

        _mixer = new MixerModel(engine);
        _mixer.InstrumentPickRequested += Mixer_InstrumentPickRequested;
        MixerItems.DataContext = _mixer;
        MixerItems.ItemsSource = _mixer.Strips;
        SongList.ItemsSource = _items;

        ApplyWindowLayout();
        ApplySettingsToEngine();
        UpdateMenuGestures();

        _engine.StateChanged += Engine_StateChanged;
        _engine.SongEnded += Engine_SongEnded;
        _engine.ChannelsChanged += Engine_ChannelsChanged;
        _engine.SongChanged += Engine_SongChanged;

        EngineText.Text = EngineFactory.IsSimulated(engine)
            ? "Simulated engine (no sound)" + (EngineFactory.FallbackReason is { } r ? $" — {r}" : "")
            : "";

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += UiTimer_Tick;
    }

    // ================================================================== startup

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BuildTree();
        try
        {
            ApplyStartupPlan(_startupPlan);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Applying the startup plan failed", ex);
            SetStatus("Startup: " + ex.Message);
        }
        SetKaraokeVisible(_settings.KaraokeScreenOn);
        UpdateModeToggles();
        _uiTimer.Start();
        SongList.Focus();
    }

    // ================================================================== settings

    private void ApplySettingsToEngine()
    {
        TryEngine(() => _engine.ChaseEvents = _settings.ChaseEvents);
        TryEngine(() => _engine.ClickEnabled = _settings.PlayClick);
        TryEngine(() =>
        {
            var drums = _engine.DrumChannels;
            for (var i = 0; i < Math.Min(16, drums.Length); i++) drums[i] = _settings.DrumChannels[i];
        });
        var portCount = 1;
        TryEngine(() =>
        {
            var available = _engine.GetOutputDevices();
            var selected = _settings.OutputDevices.Where(id => available.Any(d => d.Id == id)).ToList();
            if (selected.Count == 0 && available.Count > 0) selected.Add(available[0].Id);
            if (selected.Count > 0) _engine.SelectedOutputDevices = selected;
            portCount = Math.Max(1, selected.Count);
        });
        TryEngine(() =>
        {
            var routes = _engine.Routes;
            for (var i = 0; i < Math.Min(16, routes.Length); i++)
            {
                if (!ChannelRouteFormat.TryParse(_settings.Routes[i], out var r)) r = new ChannelRoute(0, i);
                if (r.Port >= portCount) r = r with { Port = 0 };
                routes[i] = r;
            }
        });
        _mixer.DisplayTrackNames = _settings.DisplayTrackNames;
    }

    private void ShowSettings(SettingsWindow.Page page = SettingsWindow.Page.Various)
    {
        IReadOnlyList<MidiDeviceInfo> devices = [];
        TryEngine(() => devices = _engine.GetOutputDevices());
        var dlg = new SettingsWindow(_settings, _keyMap, devices, _store.GetPlaylistNames(), page) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var result = dlg.Result;
        // Keep session state that may have changed while the dialog was open.
        result.PlaylistModeOn = _playlistModeOn;
        result.AutoplayOn = _autoOn;
        result.LastPlaylist = _settings.LastPlaylist;
        result.LastSongPath = _settings.LastSongPath;
        result.KaraokeScreenOn = _settings.KaraokeScreenOn;
        result.Window = _settings.Window;
        _settings = result;
        _keyMap = dlg.KeyMap;
        ApplySettingsToEngine();
        UpdateMenuGestures();
        SaveSettings();
        SetStatus("Settings saved.");
    }

    private void SaveSettings()
    {
        _settings.PlaylistModeOn = _playlistModeOn;
        _settings.AutoplayOn = _autoOn;
        _settings.KeyBindings = _keyMap.ToDictionary();
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Saving settings failed", ex);
            SetStatus("Could not save settings: " + ex.Message);
        }
    }

    // ================================================================== window layout

    private void ApplyWindowLayout()
    {
        var w = _settings.Window;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(w.Width, Math.Max(MinWidth, work.Width));
        Height = Math.Min(w.Height, Math.Max(MinHeight, work.Height));
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top) &&
            w.Left >= vLeft - 50 && w.Top >= vTop - 50 &&
            w.Left + 100 <= vLeft + SystemParameters.VirtualScreenWidth &&
            w.Top + 50 <= vTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = w.Left;
            Top = w.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (w.Maximized) WindowState = WindowState.Maximized;
        TreeColumn.Width = new GridLength(w.TreeWidth);
        ListColumn.Width = new GridLength(w.ListWidth);
    }

    private void CaptureWindowLayout()
    {
        var w = _settings.Window;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width > 0)
        {
            w.Left = bounds.Left;
            w.Top = bounds.Top;
            w.Width = bounds.Width;
            w.Height = bounds.Height;
        }
        w.Maximized = WindowState == WindowState.Maximized;
        w.TreeWidth = TreeColumn.ActualWidth;
        w.ListWidth = ListColumn.ActualWidth;
    }

    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _settings.Window.TreeWidth = TreeColumn.ActualWidth;
        _settings.Window.ListWidth = ListColumn.ActualWidth;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _uiTimer.Stop();
        TryEngine(() =>
        {
            if (_engine.State != PlaybackState.Stopped) _engine.Stop();
        });
        CaptureWindowLayout();
        SaveSettings();
    }

    // ================================================================== engine events (background thread → dispatcher)

    private void Engine_StateChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(UpdateTransportButtons);

    private void Engine_SongEnded(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(OnSongEnded);

    private void Engine_ChannelsChanged(object? sender, int channel) => _channelsDirty = true;

    private void Engine_SongChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            _shownSong = null; // force a refresh on the next tick
            _channelsDirty = true;
        });

    // ================================================================== UI timer (~30 Hz)

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            RefreshUi();
        }
        catch (Exception ex)
        {
            _uiTimer.Stop();
            CrashLog.Write("UI refresh failed", ex);
            SetStatus("Display refresh stopped after an error: " + ex.Message);
        }
    }

    private void RefreshUi()
    {
        if (_channelsDirty)
        {
            _channelsDirty = false;
            _mixer.RefreshFromEngine();
        }

        var song = _engine.Song;
        if (!ReferenceEquals(song, _shownSong))
        {
            _shownSong = song;
            OnSongInfoChanged(song);
        }

        var tick = _engine.PositionTicks;
        if (!_cueDragging && song is not null) CueSlider.Value = Math.Clamp(tick, CueSlider.Minimum, CueSlider.Maximum);
        TimeText.Text = FormatTime(_engine.Elapsed);
        if (!PositionBox.IsKeyboardFocusWithin) SetIfChanged(PositionBox, song is null ? "" : _engine.Position.ToString());
        if (!LeftLocBox.IsKeyboardFocusWithin) SetIfChanged(LeftLocBox, song is null ? "" : song.ToMusical(_engine.LeftLocator).ToString());
        if (!RightLocBox.IsKeyboardFocusWithin) SetIfChanged(RightLocBox, song is null ? "" : song.ToMusical(_engine.RightLocator).ToString());
        if (!TempoBox.IsKeyboardFocusWithin)
        {
            SetIfChanged(TempoBox, song is null ? "" : _engine.CurrentBpm.ToString("0.##", CultureInfo.InvariantCulture));
            var canSet = song is not null && _engine.CanSetBaseTempo;
            if (TempoBox.IsReadOnly == canSet)
            {
                TempoBox.IsReadOnly = !canSet;
                TempoBox.ToolTip = canSet
                    ? "Song tempo in BPM. Type a new tempo and press Enter (empty = file tempo)."
                    : "Song tempo in BPM (read-only: the song has several tempo changes; use Change %).";
            }
        }
        if (!TempoPercentBox.IsKeyboardFocusWithin) SetIfChanged(TempoPercentBox, FormatSigned(_engine.TempoPercent));
        if (!TransposeBox.IsKeyboardFocusWithin) SetIfChanged(TransposeBox, FormatSigned(_engine.Transpose));
        SignatureText.Text = song is null ? "-" : SignatureAt(song, tick);
        LoopToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, _engine.LoopEnabled);
        UpdateTransportButtons();

        if (Karaoke.Visibility == Visibility.Visible) Karaoke.Update(song, tick);
    }

    private static void SetIfChanged(TextBox box, string text)
    {
        if (box.Text != text) box.Text = text;
    }

    private void UpdateTransportButtons()
    {
        var state = _engine.State;
        PlayButton.SetCurrentValue(ToggleButton.IsCheckedProperty, state == PlaybackState.Playing);
        PauseButton.SetCurrentValue(ToggleButton.IsCheckedProperty, state == PlaybackState.Paused);
    }

    private void OnSongInfoChanged(IMidiSong? song)
    {
        if (song is null)
        {
            CueSlider.Maximum = 1;
            CueSlider.Value = 0;
            DurationText.Text = "0:00:00";
            SongTitleText.Text = "MIXER";
            Title = "OneStopMidi";
            WarningsText.Text = "";
            WarningsText.ToolTip = null;
            return;
        }
        CueSlider.Maximum = Math.Max(1, song.LengthTicks);
        DurationText.Text = FormatTime(song.Duration);
        var name = !string.IsNullOrWhiteSpace(song.SequenceName) ? song.SequenceName! : Path.GetFileName(song.FilePath ?? "");
        SongTitleText.Text = name;
        Title = $"{name} - OneStopMidi";
        if (_settings.ShowMidiWarnings && song.Warnings.Count > 0)
        {
            WarningsText.Text = $"⚠ {song.Warnings.Count} MIDI warning(s)";
            WarningsText.ToolTip = string.Join(Environment.NewLine, song.Warnings);
        }
        else
        {
            WarningsText.Text = "";
            WarningsText.ToolTip = null;
        }
    }

    internal static string FormatTime(TimeSpan t) =>
        $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

    private static string FormatSigned(int v) => v > 0 ? "+" + v : v.ToString(CultureInfo.InvariantCulture);

    private static string SignatureAt(IMidiSong song, long tick)
    {
        TimeSignatureChange? current = null;
        foreach (var ts in song.TimeSignatures)
        {
            if (ts.Tick <= tick) current = ts;
            else break;
        }
        return current is null ? "4/4" : $"{current.Numerator}/{current.Denominator}";
    }

    // ================================================================== mixer

    private void Mixer_InstrumentPickRequested(object? sender, ChannelStripModel strip)
    {
        var state = _engine.Channels.FirstOrDefault(c => c.Channel == strip.Channel);
        var dlg = new InstrumentPickerWindow(strip.Channel, strip.Program, state?.BankMsb ?? 0, state?.BankLsb ?? 0, strip.IsDrum) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        TryEngine(() => _engine.SetProgram(strip.Channel, dlg.SelectedProgram, dlg.BankMsb, dlg.BankLsb));
        _channelsDirty = true;
    }

    // ================================================================== helpers

    private void SetStatus(string text) => StatusText.Text = text;

    private bool TryEngine(Action action, string? what = null)
    {
        try
        {
            action();
            return true;
        }
        catch (NotImplementedException)
        {
            SetStatus($"{what ?? "This command"} is not available in this engine yet.");
        }
        catch (NotSupportedException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Engine call failed ({what})", ex);
            SetStatus($"{what ?? "Engine"}: {ex.Message}");
        }
        return false;
    }
}
