using System.Diagnostics;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace OneStopMidi.App.Common;

/// <summary>Help → Check for updates: GitHub Releases of the project repository, via Velopack.</summary>
public static class Updater
{
    public const string RepositoryUrl = "https://github.com/yarink80/OneStopMidi";

    public static async Task CheckAsync(Window owner)
    {
        UpdateManager manager;
        try
        {
            manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Update manager failed", ex);
            MessageBox.Show(owner, $"Could not check for updates:\n{ex.Message}", "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!manager.IsInstalled)
        {
            var open = MessageBox.Show(owner,
                "Automatic updates work in the installed version only.\n\nOpen the download page to get the latest release?",
                "OneStopMidi", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (open == MessageBoxResult.Yes) OpenReleasesPage();
            return;
        }

        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                MessageBox.Show(owner, $"You have the latest version ({manager.CurrentVersion}).", "OneStopMidi",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = MessageBox.Show(owner,
                $"Version {update.TargetFullRelease.Version} is available (you have {manager.CurrentVersion}).\n\nDownload it and restart now?",
                "OneStopMidi", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            await manager.DownloadUpdatesAsync(update);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Update check failed", ex);
            MessageBox.Show(owner, $"Could not check for updates:\n{ex.Message}", "OneStopMidi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl + "/releases/latest") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write("Opening the releases page failed", ex);
        }
    }
}
