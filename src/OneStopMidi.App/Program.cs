using Velopack;

namespace OneStopMidi.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: the installer/updater start the exe with --veloapp-* hooks and expect it to exit quickly.
        VelopackApp.Build().SetArgs(args).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
