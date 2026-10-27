namespace OneStopMidi.App.Common;

/// <summary>Appends unexpected errors to %APPDATA%\OneStopMidi\crash.log (never throws).</summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(AppPaths.DataFolder, "crash.log");

    public static void Write(string context, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}
