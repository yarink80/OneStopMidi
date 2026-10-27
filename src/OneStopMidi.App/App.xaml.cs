using System.Text;
using System.Windows;
using System.Windows.Threading;
using OneStopMidi.App.Common;
using OneStopMidi.App.Engine;
using OneStopMidi.App.Input;
using OneStopMidi.App.Playlists;
using OneStopMidi.App.Settings;
using OneStopMidi.App.Startup;
using OneStopMidi.App.Views;
using OneStopMidi.Core;

namespace OneStopMidi.App;

public partial class App : Application
{
    private IPlayerEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => CrashLog.Write("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            CrashLog.Write("Unobserved task exception", a.Exception);
            a.SetObserved();
        };
        KeyInput.InstallMessageHook();

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        if (settingsStore.LastLoadError is { } err)
            MessageBox.Show($"The settings file could not be read and defaults are used.\n\n{err}\n\nA copy was saved as settings.json.corrupt.",
                "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Warning);

        PlaylistStore store;
        try
        {
            store = new PlaylistStore(PlaylistStore.DefaultFolder);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Playlist folder unavailable", ex);
            store = new PlaylistStore(Path.Combine(Path.GetTempPath(), "OneStopMidi", "Playlists"));
        }

        if (!settings.FirstRunCompleted)
        {
            OfferLegacyImport(store);
            settings.FirstRunCompleted = true;
            TrySave(settingsStore, settings);
        }

        var args = CommandLineOptions.Parse(e.Args);
        if (args.Files.Count > 0)
        {
            // Expand folders; keep missing files so they show up in red.
            var expanded = new List<string>();
            foreach (var f in args.Files)
            {
                if (Directory.Exists(f)) expanded.AddRange(PlaylistStore.ExpandSongPaths([f]));
                else expanded.Add(Path.GetFullPath(f));
            }
            args = new CommandLineOptions { Playlist = args.Playlist, Files = expanded, Unknown = args.Unknown };
        }

        _engine = EngineFactory.Create();
        var plan = new StartupPlanner(store).Plan(settings, args);

        var window = new MainWindow(_engine, settings, settingsStore, store, plan);
        MainWindow = window;
        window.Show();
    }

    private static void OfferLegacyImport(PlaylistStore store)
    {
        try
        {
            if (store.GetPlaylistNames().Count > 0) return;
            var found = LegacyPlaylistFinder.Find(LegacyPlaylistFinder.DefaultRoots(), exclude: store.FolderPath);
            if (found.Count == 0) return;
            var folder = found[0];
            var count = Directory.EnumerateFiles(folder, "*.txt").Count() - 1;
            var answer = MessageBox.Show(
                $"Existing playlists were found in:\n{folder}\n\n({Math.Max(count, 0)} playlist file(s))\n\nImport them into OneStopMidi?",
                "OneStopMidi - first start", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            var created = store.ImportFolder(folder);
            MessageBox.Show($"{created.Count} playlist(s) imported.", "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CrashLog.Write("First-run playlist import failed", ex);
        }
    }

    private static void TrySave(SettingsStore store, AppSettings settings)
    {
        try
        {
            store.Save(settings);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Saving settings failed", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("UI exception", e.Exception);
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}\n\nDetails were written to {CrashLog.FilePath}.",
            "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _engine?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Engine dispose failed", ex);
        }
        base.OnExit(e);
    }
}
