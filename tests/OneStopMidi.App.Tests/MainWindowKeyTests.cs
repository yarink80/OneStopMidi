using System.Windows;
using System.Windows.Threading;
using OneStopMidi.App.Playlists;
using OneStopMidi.App.Settings;
using OneStopMidi.App.Startup;
using OneStopMidi.App.Views;
using OneStopMidi.Core;
using OneStopMidi.Core.Output;
using Xunit;

namespace OneStopMidi.App.Tests;

/// <summary>
/// End-to-end: the real MainWindow, opened through the startup planner with a three-song playlist, driven by raw
/// keyboard reports through WPF's input pipeline (no global input, nothing reaches other apps).
/// </summary>
public class MainWindowKeyTests
{
    private const int VkReturn = 0x0D, ScanEnter = 0x1C, VkUp = 0x26, ScanUp = 0x48, VkDown = 0x28, ScanDown = 0x50;

    /// <summary>Format 0, one track, a single quarter note, so the engine accepts it.</summary>
    private static void WriteTinyMidi(string path) => File.WriteAllBytes(path,
    [
        .. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 0, 96,
        .. "MTrk"u8, 0, 0, 0, 13,
        0x00, 0x90, 60, 100,
        0x60, 0x80, 60, 0,
        0x00, 0xFF, 0x2F, 0x00,
    ]);

    private static void EnsureApplication()
    {
        if (Application.Current is not null) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/OneStopMidi;component/Themes/Theme.xaml"),
        });
    }

    private static void Key(PresentationSource source, int vk, int scan, bool extended)
    {
        KeyPipelineTests.Push(source, KeyPipelineTests.RawReport(source, "KeyDown", scan, extended, vk));
        KeyPipelineTests.Push(source, KeyPipelineTests.RawReport(source, "KeyUp", scan, extended, vk));
    }

    [Fact]
    public void StartupPlaylist_LoadsFirstSong_AndArrowEnterNavigateTheList()
    {
        using var dir = new TempDir();
        var songs = new[] { "a.mid", "b.mid", "c.mid" }.Select(dir.File).ToArray();
        foreach (var s in songs) WriteTinyMidi(s);
        var store = new PlaylistStore(dir.File("Playlists"));
        store.Create("Gig", songs);

        Sta.Run(() =>
        {
            EnsureApplication();
            var settings = new AppSettings();
            settings.Startup.AutoPlay = false;
            var plan = new StartupPlanner(store).Plan(settings, new CommandLineOptions { Playlist = "Gig" });
            using var engine = new PlayerEngine(new RecordingMidiOutputProvider());
            var window = new MainWindow(engine, settings, new SettingsStore(dir.File("settings.json")), store, plan)
            {
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
            };
            window.Show();
            try
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var source = PresentationSource.FromVisual(window)!;
                KeyPipelineTests.Push(source, KeyPipelineTests.RawReport(source, "Activate", 0, false, 0));

                // Startup: the playlist is shown and its first song is loaded.
                Assert.Equal(songs[0], engine.Song?.FilePath, ignoreCase: true);

                // Down + Enter loads the next song, one row at a time.
                Key(source, VkDown, ScanDown, extended: true);
                Key(source, VkReturn, ScanEnter, extended: false);
                Assert.Equal(songs[1], engine.Song?.FilePath, ignoreCase: true);

                Key(source, VkDown, ScanDown, extended: true);
                Key(source, VkReturn, ScanEnter, extended: false);
                Assert.Equal(songs[2], engine.Song?.FilePath, ignoreCase: true);

                // Up twice from the last row, then Enter, goes back to the first song.
                Key(source, VkUp, ScanUp, extended: true);
                Key(source, VkUp, ScanUp, extended: true);
                Key(source, VkReturn, ScanEnter, extended: false);
                Assert.Equal(songs[0], engine.Song?.FilePath, ignoreCase: true);

                // NumPad Enter is a different key: by default it stops, it does not load the selection.
                Key(source, VkDown, ScanDown, extended: true);
                Key(source, VkReturn, ScanEnter, extended: true);
                Assert.Equal(songs[0], engine.Song?.FilePath, ignoreCase: true);
                Assert.Equal(PlaybackState.Stopped, engine.State);
            }
            finally
            {
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
    }
}
