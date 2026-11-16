using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace WindowsNotch.Services;

/// <summary>
/// Runs python\gesture_engine.py (OpenCV + MediaPipe) as a child process and talks to it over
/// stdin/stdout JSON lines. OpenCV reads cameras through DirectShow/Media Foundation, which gives a
/// clean picture on webcams that the browser engine decodes as striped noise.
/// </summary>
public sealed class PythonGestureHost : IGestureEngine
{
    private readonly string _python;
    private Process? _proc;
    private bool _ready, _wantRunning, _preview;
    private double _minHand = 0.30;

    public event Action<string>? StatusChanged;
    public event Action<string, double, double>? Frame;
    public event Action? PreviewClosed;

    public PythonGestureHost(string python) => _python = python;

    public static string Script => Path.Combine(AppContext.BaseDirectory, "python", "gesture_engine.py");

    /// <summary>Finds a Python that has OpenCV + MediaPipe installed, or null.</summary>
    public static async Task<string?> FindPythonAsync(string? preferred)
    {
        if (!File.Exists(Script)) return null;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) candidates.Add(preferred!);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var v in new[] { "312", "311", "310", "39" })
            candidates.Add(Path.Combine(local, "Programs", "Python", "Python" + v, "python.exe"));
        candidates.Add("python");

        foreach (var c in candidates)
        {
            if (c.Contains(Path.DirectorySeparatorChar) && !File.Exists(c)) continue;
            try
            {
                var psi = new ProcessStartInfo(c, "-c \"import cv2, mediapipe; print('ok')\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using var p = Process.Start(psi)!;
                var output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode == 0 && output.Contains("ok")) return c;
            }
            catch { /* not this one */ }
        }
        return null;
    }

    public double MinHandSize
    {
        get => _minHand;
        set { _minHand = value; Send("minhand " + value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    }

    public Task StartAsync()
    {
        _wantRunning = true;
        if (_proc == null || _proc.HasExited) Launch();
        else if (_ready) Send("start");
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _wantRunning = false;
        if (_ready) Send("stop");
        else StatusChanged?.Invoke("off");
    }

    public void SetPreview(bool on)
    {
        _preview = on;
        if (_ready) Send(on ? "preview on" : "preview off");
    }

    private void Launch()
    {
        _ready = false;
        StatusChanged?.Invoke("loading");
        try
        {
            var psi = new ProcessStartInfo(_python, $"-u \"{Script}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(Script)!,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["GLOG_minloglevel"] = "2";
            psi.Environment["TF_CPP_MIN_LOG_LEVEL"] = "2";
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) Ui(() => OnLine(e.Data)); };
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data) || e.Data.StartsWith("W0000") || e.Data.StartsWith("I0000")
                    || e.Data.StartsWith("INFO:") || e.Data.Contains("[ WARN:")) return;
                App.Log("python: " + e.Data);
            };
            _proc.Exited += (_, _) => Ui(() =>
            {
                App.Log($"python engine exited ({_proc?.ExitCode})");
                _ready = false;
                StatusChanged?.Invoke("error: gesture engine stopped");
            });
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            StatusChanged?.Invoke("error: could not start Python");
        }
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private void OnLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    MinHandSize = _minHand;
                    SetPreview(_preview);
                    StatusChanged?.Invoke("ready");
                    if (_wantRunning) Send("start");
                    break;
                case "status":
                    StatusChanged?.Invoke(root.GetProperty("state").GetString() ?? "");
                    break;
                case "frame":
                    Frame?.Invoke(root.GetProperty("gesture").GetString() ?? "None",
                        root.GetProperty("score").GetDouble(), root.GetProperty("size").GetDouble());
                    break;
                case "preview_closed":
                    _preview = false;
                    PreviewClosed?.Invoke();
                    break;
                case "diag":
                    App.Log("gesture engine: " + root.GetProperty("text").GetString());
                    break;
                case "error":
                    var msg = root.GetProperty("message").GetString();
                    App.Log("gesture: " + msg);
                    StatusChanged?.Invoke("error: " + msg);
                    break;
            }
        }
        catch { App.Log("python: " + line); }
    }

    private void Send(string cmd)
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.StandardInput.WriteLine(cmd);
                _proc.StandardInput.Flush();
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public void Dispose()
    {
        try
        {
            Send("quit");
            if (_proc is { HasExited: false } && !_proc.WaitForExit(1500)) _proc.Kill(true);
        }
        catch { }
        _proc?.Dispose();
    }
}
