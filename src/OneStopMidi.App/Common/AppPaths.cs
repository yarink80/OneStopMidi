namespace OneStopMidi.App.Common;

/// <summary>
/// Data folder: %APPDATA%\OneStopMidi, or the folder named by the ONESTOPMIDI_DATA_DIR environment variable
/// (portable use and testing).
/// </summary>
public static class AppPaths
{
    public static string DataFolder
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("ONESTOPMIDI_DATA_DIR");
            return !string.IsNullOrWhiteSpace(overridden)
                ? Path.GetFullPath(overridden)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneStopMidi");
        }
    }
}
