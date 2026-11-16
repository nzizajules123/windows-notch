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

        if (e.Args.Length >= 1 && e.Args[0] == "--lyrics-test")
        {
            // QA: resolve lyrics for "title|artist|seconds|spotify?" lines in a file, write results next to it.
            _ = Task.Run(async () =>
            {
                var svc = new Services.LyricsService();
                var outLines = new List<string>();
                foreach (var line in File.ReadAllLines(e.Args[1]))
                {
                    var p = line.Split('|');
                    if (p.Length < 4) continue;
                    var (t, a) = Services.LyricsService.Normalize(p[0], p[1], p[3] == "1");
                    var r = await svc.GetAsync(p[0], p[1], TimeSpan.FromSeconds(double.Parse(p[2])), p[3] == "1");
                    outLines.Add($"{p[0]}  /  {p[1]}\n    → \"{t}\" by \"{a}\"  →  {(r == null ? "NO LYRICS" : r.Match + $"  first line: {r.Lines.FirstOrDefault(l => l.Text != "♪")?.Text}")}");
                }
                File.WriteAllLines(e.Args[1] + ".out.txt", outLines);
                Dispatcher.Invoke(Shutdown);
            });
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
