using System.Runtime.ExceptionServices;

namespace OneStopMidi.App.Tests;

internal static class Sta
{
    /// <summary>Runs code on a fresh STA thread (WPF input objects need one) and rethrows failures.</summary>
    public static void Run(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}

/// <summary>Creates and deletes a unique temporary directory.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "onestopmidi-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}
