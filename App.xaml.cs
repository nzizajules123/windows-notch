using System.IO;
using System.Windows;

namespace WindowsNotch;

public partial class App : Application
{
    private Mutex? _mutex;

    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsNotch");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Set by "--snapshot &lt;folder&gt;": render each notch state to PNG and exit (design QA).</summary>
    public static string? SnapshotDir { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            SnapshotDir = e.Args[1];
            base.OnStartup(e);
            new NotchWindow().Show();
            return;
        }

        _mutex = new Mutex(true, "WindowsNotch.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, a) => { Log(a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

        base.OnStartup(e);
        new NotchWindow().Show();
    }

    public static void Log(object? what)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "notch.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }
}
