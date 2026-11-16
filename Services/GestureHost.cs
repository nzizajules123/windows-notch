using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WindowsNotch.Services;

/// <summary>
/// Runs Google MediaPipe's hand-gesture recognizer inside an invisible, off-screen WebView2.
/// The camera feed is never displayed; only gesture names + hand size come back.
/// </summary>
public sealed class GestureHost : IDisposable
{
    private Window? _win;
    private WebView2? _web;
    private bool _ready, _wantRunning, _initializing;

    public event Action<string>? StatusChanged;            // loading | ready | watching | off | error: …
    public event Action<string, double, double>? Frame;   // gesture, score, handSize (0..1)

    public async Task StartAsync()
    {
        _wantRunning = true;
        if (_web == null && !_initializing) await InitAsync();
        else if (_ready) Post("start");
    }

    public void Stop()
    {
        _wantRunning = false;
        if (_ready) Post("stop");
        else StatusChanged?.Invoke("off");
    }

    private async Task InitAsync()
    {
        _initializing = true;
        StatusChanged?.Invoke("loading");
        try
        {
            _web = new WebView2();
            _win = new Window
            {
                Title = "Notch Gesture Engine",
                Width = 320, Height = 240,
                Left = -32000, Top = -32000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = _web,
            };
            _win.Show();
            Native.AddExStyle(new WindowInteropHelper(_win).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);

            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsNotch", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"));
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("notch.local", Path.Combine(AppContext.BaseDirectory, "web"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Camera)
                {
                    e.State = CoreWebView2PermissionState.Allow;
                    e.Handled = true;
                }
            };
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://notch.local/gesture.html");
        }
        catch (Exception ex)
        {
            App.Log(ex);
            StatusChanged?.Invoke("error: WebView2 runtime missing or failed to start");
        }
        finally { _initializing = false; }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    _ready = true;
                    StatusChanged?.Invoke("ready");
                    if (_wantRunning) Post("start");
                    break;
                case "status":
                    StatusChanged?.Invoke(root.GetProperty("state").GetString() ?? "");
                    break;
                case "frame":
                    Frame?.Invoke(root.GetProperty("gesture").GetString() ?? "None",
                        root.GetProperty("score").GetDouble(), root.GetProperty("size").GetDouble());
                    break;
                case "error":
                    var msg = root.GetProperty("message").GetString();
                    App.Log("gesture: " + msg);
                    StatusChanged?.Invoke("error: " + msg);
                    break;
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private void Post(string message) => _web?.CoreWebView2?.PostWebMessageAsString(message);

    public void Dispose()
    {
        try { Post("stop"); } catch { }
        _web?.Dispose();
        _win?.Close();
    }
}
