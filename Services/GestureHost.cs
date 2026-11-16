using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
            _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 11, 13) };
            _win = new Window
            {
                Title = "Gesture camera",
                Width = PreviewW, Height = PreviewH,
                Left = -32000, Top = -32000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(11, 11, 13)),
                Content = BuildChrome(_web),
            };
            _win.Show();
            Native.AddExStyle(new WindowInteropHelper(_win).Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            // Windows 11 rounded corners for the preview card
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(new WindowInteropHelper(_win).Handle, 33, ref round, sizeof(int));
            ApplyPreview();

            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsNotch", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir, new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows " +
                "--disable-features=CalculateNativeWinOcclusion"));
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
                    Post("minhand:" + MinHandSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Post("preview:" + (_preview ? "on" : "off"));
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
        catch (Exception ex) { App.Log(ex); }
    }

    private void Post(string message) => _web?.CoreWebView2?.PostWebMessageAsString(message);

    // ------------------------------------------------------------------ live preview
    private const double PreviewW = 260, PreviewH = 222; // 26 px title bar + 4:3 video
    private bool _preview;
    public double MinHandSize { get; set; } = 0.30;

    /// <summary>Raised when the user closes the preview with its ✕ button.</summary>
    public event Action? PreviewClosed;

    /// <summary>Shows the camera in a small always-on-top card in the bottom-right corner (or hides it off-screen).</summary>
    public void SetPreview(bool on)
    {
        _preview = on;
        ApplyPreview();
    }

    private void ApplyPreview()
    {
        if (_win == null) return;
        if (_preview)
        {
            var wa = SystemParameters.WorkArea;
            if (_win.Left < wa.Left - 1000) // first show → park in the corner
            {
                _win.Left = wa.Right - PreviewW - 16;
                _win.Top = wa.Bottom - PreviewH - 16;
            }
        }
        else
        {
            _win.Left = -32000;
            _win.Top = -32000;
        }
        if (_ready) Post("preview:" + (_preview ? "on" : "off"));
    }

    private UIElement BuildChrome(WebView2 web)
    {
        var title = new TextBlock
        {
            Text = "●  Gesture camera",
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        var close = new Button
        {
            Content = "✕", Width = 26, Height = 26, FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Hide camera preview (gestures keep working)",
        };
        close.Click += (_, _) => { SetPreview(false); PreviewClosed?.Invoke(); };

        var bar = new Grid
        {
            Height = 26,
            Background = new LinearGradientBrush(Color.FromRgb(0x1C, 0x1C, 0x22), Color.FromRgb(0x11, 0x11, 0x14), 90),
            Cursor = System.Windows.Input.Cursors.SizeAll,
        };
        bar.Children.Add(title);
        bar.Children.Add(close);
        bar.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) try { _win?.DragMove(); } catch { } };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(web);
        return root;
    }

    public void Dispose()
    {
        try { Post("stop"); } catch { }
        _web?.Dispose();
        _win?.Close();
    }
}
