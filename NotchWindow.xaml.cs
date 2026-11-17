using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WindowsNotch.Services;
using WindowsNotch.UI;

namespace WindowsNotch;

public partial class NotchWindow : Window
{
    // ---- geometry ---------------------------------------------------------
    private const double CollapsedW = 236, CollapsedWMedia = 304, CollapsedWTicker = 390, HudW = 330;
    private const double CollapsedH = 32, TickerH = 29;
    private const double PeekW = 420, PeekH = 66, PeekRadius = 28;
    private const double ExpandedW = 580, ExpandedH = 264, ExpandedHLyrics = 304, ExpandedRadius = 34;
    private const double CollapsedRadius = 16;

    private enum Mode { Collapsed, Peek, Expanded }

    // ---- services ---------------------------------------------------------
    private readonly Settings _settings = Settings.Load();
    private readonly AccentTheme _theme;
    private readonly MediaService _media = new();
    private readonly LyricsService _lyrics = new();
    private readonly CaptionService _captions = new();
    private readonly AppsService _apps = new();
    private readonly ShelfService _shelf = new();
    private IGestureEngine? _gestures;
    private readonly VolumeWatcher _volume = new();
    private readonly BrightnessWatcher _brightness = new();
    private readonly NetworkWatcher _network = new();
    private readonly NotificationWatcher _notifications = new();
    private readonly DispatcherTimer _hudTimer = new();
    private bool _hudShown, _eyesDancing, _blurOn;
    private readonly DeviceWatcher _devices = new();
    private UIElement? _card;                                  // view shown in Peek mode (peek / charge / device)
    // the island's tint over the acrylic blur: solid while collapsed, frosted when open
    private readonly SolidColorBrush _glass = new(Color.FromRgb(0, 0, 0));
    private static readonly Color GlassCollapsed = Color.FromArgb(0xFF, 0, 0, 0);
    private static readonly Color GlassPeek = Color.FromArgb(0xC4, 0x0E, 0x0E, 0x12);
    private static readonly Color GlassOpen = Color.FromArgb(0xAE, 0x0E, 0x0E, 0x12);
    private float _lastVolume = -1;

    // ---- state ------------------------------------------------------------
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hoverIntent = new() { Interval = TimeSpan.FromMilliseconds(130) };
    private readonly DispatcherTimer _collapseTimer = new();
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IntPtr _hwnd;
    private Mode _mode = Mode.Collapsed;
    private bool Expanded => _mode == Mode.Expanded;
    private bool _draggingOut, _peekIsMedia, _artPlaying;
    private double _targetW = -1, _targetH = -1, _liveGlowTarget = -1;
    private int _tick, _tabIndex;
    private DateTime _lastPlaying = DateTime.MinValue;
    private string? _lastTrackKey, _lyricsKey;
    private LyricsResult? _currentLyrics;
    private string _captionLine = "", _captionStatus = "", _shownLine = "", _shownTicker = "";
    private bool? _lastCharging;

    // karaoke highlight: sung part glows white→accent, the rest stays dim; the two middle stops sweep left→right
    private readonly GradientStop _kSung = new(Colors.White, 0), _kEdge = new(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0.03);
    private LinearGradientBrush _karaoke = null!;
    private int _karaIdx = -2;
    private bool _karaRunning, _tickerIsLyric, _lineIsLyric;
    private int _countdown;
    private Action? _countdownAction;
    private string _gestureState = "off";

    // gesture hold tracking
    private string _gCur = "None";
    private DateTime _gSince, _gCooldown, _gNextTrace;
    private int _gFrames;
    private (string gesture, double progress)? _hold;

    // shelf drag-out tracking
    private Point _dragStart;
    private ShelfItem? _dragItem;

    // animatable corner radius of the island (drives CornerRadius + clip)
    public static readonly DependencyProperty NotchRadiusProperty = DependencyProperty.Register(
        nameof(NotchRadius), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(CollapsedRadius, (d, _) => ((NotchWindow)d).ApplyShape()));
    public double NotchRadius { get => (double)GetValue(NotchRadiusProperty); set => SetValue(NotchRadiusProperty, value); }

    public NotchWindow()
    {
        InitializeComponent();
        _theme = new AccentTheme(Resources); // DynamicResource picks the live brushes up from here

        foreach (var fx in new[] { NotchGlowFx, LiveGlowFx, HoldGlowFx, PeekGlowFx, ArtGlowFx, ProgGlowFx, DropGlowFx, HudGlowFx, HudBarGlowFx, Eyes.Glow })
            _theme.RegisterGlow(fx);
        _hudTimer.Tick += (_, _) => { _hudTimer.Stop(); HideHud(); };
        Notch.Background = _glass;
        EarL.Fill = _glass;
        EarR.Fill = _glass;
        _theme.Changed += (c, ms) => Eyes.SetIris(c, ms);
        Eyes.Interacted += () => _hoverIntent.Stop();

        _kSung.Color = AccentTheme.Lighter(AccentTheme.Default, 0.35);
        _karaoke = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.White, 0), _kSung, _kEdge, new(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1),
        }, new Point(0, 0), new Point(1, 0));
        SpotStop.Color = AccentTheme.WithAlpha(AccentTheme.Default, 0x48);
        _theme.Changed += (c, ms) =>
        {
            Motion.Color(_kSung, GradientStop.ColorProperty, AccentTheme.Lighter(c, 0.35), ms);
            Motion.Color(SpotStop, GradientStop.ColorProperty, AccentTheme.WithAlpha(c, 0x48), ms);
        };

        AppsList.ItemsSource = _apps.Items;
        ShelfList.ItemsSource = _shelf.Items;
        _shelf.Items.CollectionChanged += (_, _) => UpdateShelfState();
        UpdateShelfState();

        _collapseTimer.Interval = TimeSpan.FromSeconds(_settings.CollapseDelaySec);
        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); if (!Notch.IsMouseOver) Collapse(); };
        _hoverIntent.Tick += (_, _) =>
        {
            _hoverIntent.Stop();
            if (!Notch.IsMouseOver) return;
            // hovering / poking the eyes plays with them instead of opening the notch
            if (Eyes.IsMouseOver && _mode == Mode.Collapsed) { _hoverIntent.Start(); return; }
            Expand();
        };
        _peekTimer.Tick += (_, _) => { _peekTimer.Stop(); EndPeek(); };
        _clock.Tick += (_, _) => OnTick();
        _countdownTimer.Tick += (_, _) => OnCountdownTick();
        Notch.SizeChanged += (_, _) => ApplyShape();

        LyricsToggle.IsChecked = _settings.LyricsEnabled;
        CcToggle.IsChecked = _settings.CaptionsEnabled;
        GestureSwitch.IsChecked = _settings.GesturesEnabled;
        PreviewChip.IsChecked = _settings.ShowCameraPreview;
        StartupSwitch.IsChecked = PowerService.StartsWithWindows;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) => Cleanup();
    }

    // =======================================================================
    //  Window plumbing
    // =======================================================================
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        PositionWindow();
        if (App.SnapshotDir == null)
        {
            try { _backing = new BlurBacking(); } catch (Exception ex) { App.Log("blur backing: " + ex.Message); }
            SetBlur(true);
        }
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnDisplayChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(PositionWindow);

    private void PositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartAmbientLoops();
        UpdateBattery();
        UpdateClock();
        ApplyCollapsed(animate: false);

        // Entrance: the island drops in from the top edge.
        var drop = new TranslateTransform(0, -40);
        NotchHost.RenderTransform = drop;
        Motion.To(drop, TranslateTransform.YProperty, 0, 700, Motion.Spring(0.4), 150);
        // keep the blur region glued to the island while it drops in
        EventHandler follow = (_, _) => UpdateRegion();
        CompositionTarget.Rendering += follow;
        var stopFollow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        stopFollow.Tick += (_, _) => { stopFollow.Stop(); CompositionTarget.Rendering -= follow; UpdateRegion(); };
        stopFollow.Start();

        _media.Changed += OnMediaChanged;
        try { await _media.InitAsync(); }
        catch (Exception ex) { App.Log("media init: " + ex); }

        _captions.LineChanged += line => Dispatcher.BeginInvoke(() => { _captionLine = line; RefreshLiveLine(); });
        _captions.StatusChanged += st => Dispatcher.BeginInvoke(() => { _captionStatus = st; RefreshLiveLine(); });
        if (_settings.CaptionsEnabled) _captions.Start();

        if (App.SnapshotDir != null) { _clock.Start(); await RunSnapshotsAsync(App.SnapshotDir); return; }

        StartSystemSignals();

        // If we start while the PC is already locked, wait for the unlock event instead.
        if (_settings.GesturesEnabled && !Native.IsSessionLocked()) await StartGesturesAsync();
        UpdateGestureUi();

        _clock.Start();
        OnMediaChanged();
    }

    /// <summary>Design QA: renders collapsed / peek / every tab / countdown to PNGs, then exits.</summary>
    private async Task RunSnapshotsAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        void Save(string name)
        {
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            // dark-grey "desktop" behind the island so its edges are visible
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4B)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            rtb.Render(dv);
            rtb.Render(Root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }
        try
        {
            await Task.Delay(1500); Save("1-collapsed");
            Eyes.Express(EyeMood.Happy, 60000);
            await Task.Delay(700); Save("1b-eyes-happy");
            Eyes.Express(EyeMood.Neutral, 10);
            ShowHud("", "Volume", 0.62, "62", 60000);
            await Task.Delay(900); Save("1c-hud-volume");
            ShowHud("", "Home Wi-Fi", null, "", 60000);
            Eyes.Express(EyeMood.Happy, 60000);
            await Task.Delay(900); Save("1d-hud-wifi");
            HideHud(); Eyes.Express(EyeMood.Neutral, 10);
            await Task.Delay(800);
            ShowPeek("Ana", "See you at 7? 🍕", AppIconFor("explorer"), "", media: false, ms: 60000, app: "WhatsApp  ·  now");
            await Task.Delay(1200); Save("1e-notification");
            ShowCharge(true, 79);
            await Task.Delay(1500); Save("1f-charging");
            ShowCharge(false, 79);
            await Task.Delay(1400); Save("1g-unplugged");
            ShowDevice(new DeviceEvent(true, "SanDisk Ultra", "USB drive", ""));
            await Task.Delay(1300); Save("1h-usb");
            EndPeek();
            await Task.Delay(900);
            Eyes.Express(EyeMood.Love, 60000);
            await Task.Delay(800); Save("1i-eyes-love");
            Eyes.Express(EyeMood.Sleepy, 60000);
            await Task.Delay(800); Save("1j-eyes-sleepy");
            Eyes.Express(EyeMood.Neutral, 10);
            _theme.Set(Color.FromRgb(0xFF, 0x5E, 0x9A), 1);
            ShowPeek("Blinding Lights", "The Weeknd  ·  Spotify", null, "", media: true, ms: 60000);
            PeekEq.IsPlaying = true;
            await Task.Delay(1400); Save("2-peek");
            Expand();
            await Task.Delay(1500); Save("3-media");
            foreach (var (tab, name) in new[] { (TabApps, "4-apps"), (TabShelf, "5-shelf"), (TabPower, "6-control") })
            {
                tab.IsChecked = true;
                await Task.Delay(1300); Save(name);
            }
            StartCountdown("Restarting", "Restart now", "Close your hand ✊ or press Cancel to stop", () => { });
            await Task.Delay(1600); Save("7-countdown");
            CancelCountdown();
            TabMedia.IsChecked = true;
            await Task.Delay(900); Save("8-lyrics-stage");
            Collapse();
            await Task.Delay(1800); Save("9-pill-lyrics");
        }
        catch (Exception ex) { App.Log("snapshot: " + ex); }
        Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _captions.Stop();
        _gestures?.Dispose();
        _volume.Dispose();
        _brightness.Dispose();
        _notifications.Dispose();
        _devices.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _backing?.Dispose();
    }

    // =======================================================================
    //  System signals → eyes + HUD (volume, brightness, network, notifications)
    // =======================================================================
    private void StartSystemSignals()
    {
        try
        {
            _volume.Changed += OnVolume;
            _volume.Start();
        }
        catch (Exception ex) { App.Log("volume: " + ex.Message); }
        try
        {
            _brightness.Changed += b => Dispatcher.BeginInvoke(() => OnBrightness(b));
            _brightness.Start();
        }
        catch (Exception ex) { App.Log("brightness: " + ex.Message); }
        try
        {
            _network.Changed += (online, name, wifi) => Dispatcher.BeginInvoke(() => OnNetwork(online, name, wifi));
            _network.Start();
        }
        catch (Exception ex) { App.Log("network: " + ex.Message); }
        try
        {
            _notifications.Received += (app, title, body) => Dispatcher.BeginInvoke(() => OnNotification(app, title, body));
            _notifications.Start();
        }
        catch (Exception ex) { App.Log("notifications: " + ex.Message); }
        try
        {
            _devices.Changed += d => Dispatcher.BeginInvoke(() => ShowDevice(d));
            _devices.Start();
        }
        catch (Exception ex) { App.Log("devices: " + ex.Message); }
        // charger plugged / unplugged → react instantly instead of waiting for the next battery poll
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) Dispatcher.BeginInvoke(UpdateBattery);
    }

    private void OnVolume(float level, bool muted)
    {
        var pct = (int)Math.Round(level * 100);
        var glyph = muted || pct == 0 ? "" : pct < 34 ? "" : pct < 67 ? "" : "";
        var dir = _lastVolume < 0 ? 0 : Math.Sign(level - _lastVolume);
        _lastVolume = level;
        ShowHud(glyph, muted ? "Muted" : "Volume", muted ? 0 : level, muted ? "" : pct.ToString());
        if (muted) Eyes.Express(EyeMood.Sleepy, 1400);
        else if (pct >= 100) { Eyes.Express(EyeMood.Surprised, 900); Eyes.Jump(1.2); }
        else Eyes.Nod(-dir);
    }

    private void OnBrightness(int pct)
    {
        ShowHud(pct < 40 ? "" : "", "Brightness", pct / 100.0, pct.ToString());
        if (pct <= 15) Eyes.Express(EyeMood.Sleepy, 1400);
        else if (pct >= 95) Eyes.Express(EyeMood.Surprised, 900);
        else Eyes.Blink();
    }

    private void OnNetwork(bool online, string name, bool wifi)
    {
        App.Log($"network: {(online ? "online" : "offline")} {name}");
        if (online)
        {
            ShowHud(wifi ? "" : "", string.IsNullOrEmpty(name) ? (wifi ? "Wi-Fi" : "Ethernet") : name, null, "Connected", 2200);
            Eyes.Express(EyeMood.Happy, 1800);
            Eyes.Jump(1.1);
        }
        else
        {
            ShowHud("", "Offline", null, "No internet", 2200);
            Eyes.Express(EyeMood.Sad, 2400);
        }
    }

    private void OnNotification(string app, string title, string body)
    {
        if (Expanded || _countdown > 0) return;
        Eyes.Jump();
        Eyes.Express(EyeMood.Surprised, 700);
        Eyes.Attend(4, 0, 700);
        var head = string.IsNullOrWhiteSpace(title) ? app : title;
        var sub = string.IsNullOrWhiteSpace(body) || body == head ? "" : body;
        var icon = AppIconFor(app);
        var appLine = string.IsNullOrWhiteSpace(app) ? "Notification  ·  now" : $"{app}  ·  now";
        // let the eyes react first, then open the bubble
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        t.Tick += (_, _) => { t.Stop(); ShowPeek(head, sub, icon, "\uEA8F", media: false, ms: 5200, app: appLine); };
        t.Start();
    }

    /// <summary>Finds the icon of the app that sent a notification (by matching a running process).</summary>
    private static ImageSource? AppIconFor(string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return null;
        var key = new string(app.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session) continue;
                var pn = p.ProcessName.ToLowerInvariant();
                if (!(pn == key || (key.Length >= 4 && (pn.Contains(key) || key.Contains(pn) && pn.Length >= 4))))
                {
                    // fall back to the file description ("Google Chrome", "WhatsApp"…) only for windowed apps
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    var d = p.MainModule?.FileVersionInfo.FileDescription ?? "";
                    var dk = new string(d.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    if (dk != key && !(key.Length >= 4 && dk.Contains(key))) continue;
                }
                var path = p.MainModule?.FileName;
                if (path == null) continue;
                var large = new IntPtr[1];
                if (Native.ExtractIconEx(path, 0, large, null, 1) > 0) return Native.IconToBitmap(large[0], true);
            }
            catch { /* protected / exited process */ }
            finally { p.Dispose(); }
        }
        return null;
    }

    // =======================================================================
    //  Power: charger plugged / unplugged
    // =======================================================================
    private void ShowCharge(bool charging, int pct)
    {
        Eyes.Express(charging ? EyeMood.Love : EyeMood.Surprised, 1800);
        Eyes.Jump(1.15);
        var green = Color.FromRgb(0x30, 0xD1, 0x58);
        ChargeTitle.Text = charging ? "Charging" : "On battery";
        if (charging) ChargeSub.Text = "Power adapter connected";
        else
        {
            var s = PowerService.SecondsLeft();
            ChargeSub.Text = s > 0 ? $"About {(s / 3600 > 0 ? $"{s / 3600} h " : "")}{s % 3600 / 60} min left" : "Running on battery power";
        }
        ChargePct.Foreground = new SolidColorBrush(charging ? Color.FromRgb(0x5E, 0xE0, 0x82) : pct <= 20 ? Color.FromRgb(0xFF, 0x5A, 0x4F) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        ChargeFill.Background = new SolidColorBrush(charging ? green : pct <= 20 ? Color.FromRgb(0xFF, 0x45, 0x3A)
            : pct <= 35 ? Color.FromRgb(0xFF, 0xB0, 0x20) : Color.FromRgb(0xF2, 0xF4, 0xF7));
        _peekIsMedia = false;
        if (!ShowCard(ChargeView, 372, 70, 3600)) return;

        const double fillMax = 44 - 2 * 1.4 - 2 * 2.4;
        var target = Math.Max(3, fillMax * pct / 100.0);
        if (charging)
        {
            // battery fills up from empty, the bolt bursts in, rings ripple out, the number counts up
            ChargeFill.BeginAnimation(WidthProperty, new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(1100))
            { BeginTime = TimeSpan.FromMilliseconds(180), EasingFunction = Motion.OutQuint });
            ChargeBolt.BeginAnimation(OpacityProperty, null);
            ChargeBolt.Opacity = 1;
            ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Motion.Pop(ChargeBoltScale, 0, 1, 700, 0.9);
            ChargeBoltFx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) });
            foreach (var (ring, scale, delay) in new[] { (ChargeRing1, ChargeRing1Scale, 250), (ChargeRing2, ChargeRing2Scale, 650) })
            {
                ring.Stroke = new SolidColorBrush(green);
                var grow = new DoubleAnimation(0.7, 2.1, TimeSpan.FromMilliseconds(1100))
                { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Motion.Out, RepeatBehavior = new RepeatBehavior(2) };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(1100))
                { BeginTime = TimeSpan.FromMilliseconds(delay), RepeatBehavior = new RepeatBehavior(2) });
            }
            CountUp(ChargePct, Math.Max(0, pct - 25), pct, 1100);
        }
        else
        {
            // unplugged: the bolt shrinks away, the level settles down from full, no ripples
            ChargeFill.BeginAnimation(WidthProperty, new DoubleAnimation(fillMax, target, TimeSpan.FromMilliseconds(900))
            { BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = Motion.Out });
            var shrink = new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.6 } };
            ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            ChargeBoltScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            ChargeBolt.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(420)));
            ChargeBoltFx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            foreach (var r in new[] { ChargeRing1, ChargeRing2 }) { r.BeginAnimation(OpacityProperty, null); r.Opacity = 0; }
            ChargePct.Text = $"{pct}%";
            Motion.SlideIn(ChargePct, 8, 400, 200);
        }
    }

    private static void CountUp(TextBlock tb, int from, int to, int ms)
    {
        var start = DateTime.Now;
        tb.Text = $"{from}%";
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        t.Tick += (_, _) =>
        {
            var k = Math.Min(1, (DateTime.Now - start).TotalMilliseconds / ms);
            tb.Text = $"{(int)Math.Round(from + (to - from) * (1 - Math.Pow(1 - k, 3)))}%";
            if (k >= 1) t.Stop();
        };
        t.Start();
    }

    // =======================================================================
    //  Devices: USB drive / phone / headset … plugged in or removed
    // =======================================================================
    private void ShowDevice(DeviceEvent d)
    {
        var accent = d.Connected ? Color.FromRgb(0x5A, 0xB0, 0xFF) : Color.FromRgb(0x9A, 0x9A, 0xA2);
        DeviceGlyph.Text = d.Glyph;
        DeviceGlyph.Foreground = new SolidColorBrush(accent);
        DeviceTitle.Text = d.Connected ? "Connected" : "Disconnected";
        DeviceSub.Text = d.Name;
        DeviceBadgeText.Text = d.Kind;
        DeviceBadgeText.Foreground = DeviceGlyph.Foreground;
        DeviceBadge.Background = new SolidColorBrush(d.Connected ? Color.FromArgb(0x26, 0x0A, 0x84, 0xFF) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        _peekIsMedia = false;
        Eyes.Express(d.Connected ? EyeMood.Happy : EyeMood.Sad, 1600);
        if (!ShowCard(DeviceView, 372, 70, 3000)) return;

        if (d.Connected)
        {
            // the cable slides in, then the icon "plugs" in with a spark
            DeviceCable.BeginAnimation(WidthProperty, new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(380)) { EasingFunction = Motion.Out });
            DeviceIconX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(620))
            { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } });
            DeviceIconBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(120) });
            var spark = new DoubleAnimationUsingKeyFrames();
            spark.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430))));
            spark.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500))));
            spark.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
            DeviceSpark.BeginAnimation(OpacityProperty, spark);
            Motion.Pop(DeviceSparkScale, 0.3, 1.8, 900, 0.2);
        }
        else
        {
            // removed: the icon pops out to the right and dims, the cable retracts
            DeviceCable.BeginAnimation(WidthProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(250) });
            DeviceIconX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(500))
            { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseIn } });
            DeviceIconBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(500)));
            DeviceSpark.BeginAnimation(OpacityProperty, null);
            DeviceSpark.Opacity = 0;
        }
        Motion.SlideIn(DeviceTitle, 8, 360, 160);
    }

    /// <summary>Volume / brightness / network: icon + label on the left, bar + value on the right, eyes in the middle.</summary>
    private void ShowHud(string glyph, string label, double? value, string valueText, int ms = 1600)
    {
        if (_mode != Mode.Collapsed) return;
        HudIcon.Text = glyph;
        HudLabel.Text = label;
        HudValue.Text = valueText;
        HudTrack.Visibility = value.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (value.HasValue)
            Motion.To(HudFill, WidthProperty, Math.Clamp(value.Value, 0, 1) * HudTrack.Width, 220, Motion.Out);

        if (!_hudShown)
        {
            _hudShown = true;
            if (value.HasValue) HudFill.Width = Math.Clamp(value.Value, 0, 1) * HudTrack.Width;
            Motion.Hide(MiniLeft, 120);
            Motion.Hide(MiniRight, 120);
            foreach (var el in new UIElement[] { HudLeft, HudRight })
            {
                el.Visibility = Visibility.Visible;
                el.Opacity = 0;
                Motion.Show(el, 260, 90, blur: 6);
            }
            Motion.Pop(HudIconScale, 0.4, 1, 520, 0.7);
            _targetW = _targetH = -1;
            Morph(HudW, CollapsedH + (Ticker.Visibility == Visibility.Visible ? TickerH : 0), CollapsedRadius, 440, 0.3);
        }
        else Motion.Pop(HudIconScale, 0.85, 1, 300, 0.5);

        _hudTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void HideHud()
    {
        if (!_hudShown) return;
        _hudShown = false;
        Motion.Hide(HudLeft, 120);
        Motion.Hide(HudRight, 120);
        foreach (var el in new UIElement[] { MiniLeft, MiniRight })
        {
            el.Visibility = Visibility.Visible;
            el.Opacity = 0;
            Motion.Show(el, 260, 110, blur: 6);
        }
        _targetW = _targetH = -1;
        ApplyCollapsed(animate: true);
    }

    /// <summary>Eyes glance at the mouse when it comes near the top of the screen.</summary>
    private void TrackCursorWithEyes()
    {
        if (_mode != Mode.Collapsed || !Native.GetCursorPos(out var p)) return;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null) return;
        var eyes = Eyes.PointToScreen(new Point(Eyes.ActualWidth / 2, Eyes.ActualHeight / 2));
        var dx = p.X - eyes.X;
        var dy = p.Y - eyes.Y;
        if (Math.Abs(dx) > 700 || dy > 420 || (Math.Abs(dx) < 2 && Math.Abs(dy) < 2)) return;
        Eyes.Attend(Math.Clamp(dx / 90, -4, 4), Math.Clamp(dy / 120, -1, 2), 600);
    }

    private void OnTick()
    {
        _tick++;
        UpdateClock();
        UpdateProgress();
        RefreshLiveLine();
        TrackCursorWithEyes();

        if (_tick % 8 == 0)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            VerifyRegion();
            if (Expanded && TabApps.IsChecked == true) RefreshApps();
        }
        if (_tick % 4 == 0) UpdateFullscreenFade();
        if (_tick % 20 == 0) UpdateBattery();
    }

    /// <summary>Loops that run forever but only cost anything while their element is visible.</summary>
    private void StartAmbientLoops()
    {
        LiveGlowLine.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(1.6))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });

        DropAnts.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -18, TimeSpan.FromSeconds(0.7))
        { RepeatBehavior = RepeatBehavior.Forever });
        DropArrowY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, 5, TimeSpan.FromSeconds(0.6))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });

        CountdownGlowStop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(
            Color.FromArgb(0x70, 0xFF, 0x45, 0x3A), Color.FromArgb(0x22, 0xFF, 0x45, 0x3A), TimeSpan.FromSeconds(0.5))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
    }

    private void SetAmbientDrift(bool on)
    {
        if (on)
        {
            AmbientScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.35, 1.65, TimeSpan.FromSeconds(9))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
            AmbientScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.35, 1.65, TimeSpan.FromSeconds(9))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
            AmbientRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-8, 10, TimeSpan.FromSeconds(13))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
        }
        else
        {
            AmbientScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            AmbientScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            AmbientRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    // =======================================================================
    //  Shape: morphing size + radius, clipped so the blurred art never leaks
    // =======================================================================
    private void Morph(double w, double h, double radius, int ms, double bounce)
    {
        if (Math.Abs(w - _targetW) < 0.5 && Math.Abs(h - _targetH) < 0.5) return;
        _targetW = w; _targetH = h;
        if (ms <= 0)
        {
            Notch.BeginAnimation(WidthProperty, null); Notch.Width = w;
            Notch.BeginAnimation(HeightProperty, null); Notch.Height = h;
            BeginAnimation(NotchRadiusProperty, null); NotchRadius = radius;
            return;
        }
        // Width leads, height follows a beat later: reads as one fluid "blob".
        Motion.To(Notch, WidthProperty, w, ms, Motion.Spring(bounce));
        Motion.To(Notch, HeightProperty, h, ms + 60, Motion.Spring(bounce * 0.8), 25);
        Motion.To(this, NotchRadiusProperty, radius, ms, Motion.Out);
    }

    private void ApplyShape()
    {
        var r = NotchRadius;
        Notch.CornerRadius = new CornerRadius(0, 0, r, r);
        double w = Notch.ActualWidth, h = Notch.ActualHeight;
        if (w <= 0 || h <= 0) return;
        r = Math.Min(r, Math.Min(w, h) / 2);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true, true);
            c.LineTo(new Point(w, 0), false, false);
            c.LineTo(new Point(w, h - r), false, false);
            c.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r, h), false, false);
            c.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        NotchContent.Clip = g;
        UpdateRegion();
    }

    // =======================================================================
    //  Frosted backdrop: acrylic blur clipped to the island's outline
    // =======================================================================
    private bool _wantBlur;
    private BlurBacking? _backing;

    /// <summary>The frosted blur lives in a separate window under the island (see <see cref="BlurBacking"/>).</summary>
    private void SetBlur(bool on)
    {
        _wantBlur = on;
        _blurOn = on;
        if (_backing == null) return;
        if (on) UpdateRegion();
        else _backing.Hide();
    }

    private void UpdateRegion()
    {
        if (_hwnd == IntPtr.Zero || App.SnapshotDir != null || !IsLoaded) return;
        double w = Notch.ActualWidth, h = Notch.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var p = Notch.TranslatePoint(new Point(0, 0), Root);
        var r = Math.Min(NotchRadius, Math.Min(w, h) / 2);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        try
        {
            // clip the main window to the island too (hit-testing + no stray shadow under the glass)
            Backdrop.SetShape(_hwnd, p.X * sx, p.Y * sy, w * sx, h * sy, r * sx, 12 * sx);
            if (_wantBlur && _backing != null && Native.GetWindowRect(_hwnd, out var win))
                _backing.Place(_hwnd, win.Left + (int)Math.Round(p.X * sx), win.Top + (int)Math.Round(p.Y * sy),
                    (int)Math.Round(w * sx), (int)Math.Round(h * sy), r * sx, (int)Math.Round(12 * sx));
        }
        catch (Exception ex) { App.Log("region: " + ex.Message); }
    }

    /// <summary>Keeps the blur window glued under the notch (z-order can be shuffled by other topmost windows).</summary>
    private void VerifyRegion() { if (_wantBlur) UpdateRegion(); }

    private void SetGlass(Mode mode) => Motion.Color(_glass, SolidColorBrush.ColorProperty,
        mode switch { Mode.Expanded => GlassOpen, Mode.Peek => GlassPeek, _ => GlassCollapsed }, 380);

    // =======================================================================
    //  Expand / peek / collapse
    // =======================================================================
    private void Notch_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (!Expanded) _hoverIntent.Start();
    }

    private void Notch_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverIntent.Stop();
        if (Expanded) RestartCollapseTimer();
    }

    private void RestartCollapseTimer()
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void Expand()
    {
        if (Expanded) return;
        var wasPeek = _mode == Mode.Peek;
        _mode = Mode.Expanded;
        _peekTimer.Stop();
        _collapseTimer.Stop();
        SetGlass(Mode.Expanded);
        SetBlur(true);
        Motion.To(NotchHost, OpacityProperty, 1, 150);

        if (TabApps.IsChecked == true) RefreshApps();
        UpdateMediaPanel(trackChanged: false);

        Morph(ExpandedW, ExpandedHeight, ExpandedRadius, 560, 0.32);
        Motion.Hide(CollapsedView, 110);
        if (wasPeek && _card != null) { Motion.Hide(_card, 110); _card = null; }
        ExpandedView.Opacity = 0;
        Motion.Show(ExpandedView, 320, 150, blur: 14, fromY: -12);

        Motion.To(Ambient, OpacityProperty, 1, 600, Motion.Out, 120);
        Motion.To(Rim, OpacityProperty, 1, 400, Motion.Out, 150);
        Motion.To(NotchShadow, OpacityProperty, 1, 400);
        Motion.To(NotchGlow, OpacityProperty, 0.85, 700, Motion.Out, 100);
        Motion.To(LiveGlowHost, OpacityProperty, 0, 150);
        _liveGlowTarget = -1;
        SetAmbientDrift(true);

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            MoveTabIndicator(animate: false);
            StaggerCurrentPanel();
        });
    }

    private void Collapse()
    {
        if (!Expanded || _countdown > 0 || _draggingOut) return;
        _mode = Mode.Collapsed;
        SetGlass(Mode.Collapsed);
        Motion.Hide(ExpandedView, 130, blur: 12);
        Motion.To(Ambient, OpacityProperty, 0, 220, done: () => { if (!Expanded) SetAmbientDrift(false); });
        Motion.To(Rim, OpacityProperty, 0, 200);
        Motion.To(NotchShadow, OpacityProperty, 0, 300);
        Motion.To(NotchGlow, OpacityProperty, 0, 300);
        CollapsedView.Opacity = 0;
        Motion.Show(CollapsedView, 260, 170, blur: 8);
        _targetW = _targetH = -1;
        ApplyCollapsed(animate: true);
    }

    private void ShowPeek(string title, string sub, ImageSource? art, string glyph, bool media, int ms = 3200, string app = "")
    {
        if (Expanded || _countdown > 0) return;
        var same = _mode == Mode.Peek && _card == PeekView;
        PeekTitle.Text = title;
        PeekSub.Text = sub;
        PeekApp.Text = app;
        PeekApp.Visibility = app.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PeekArtBrush.ImageSource = art;
        PeekArtBrush.Stretch = media ? Stretch.UniformToFill : Stretch.Uniform;
        PeekArt.Margin = new Thickness(media ? 0 : 8);
        PeekArt.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
        PeekGlyph.Text = glyph;
        PeekEq.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        PeekEq.IsPlaying = media && _media.Current?.IsPlaying == true;
        _peekIsMedia = media;

        ShowCard(PeekView, PeekW, app.Length > 0 ? PeekH + 10 : PeekH, ms);
        if (same) Motion.SlideIn(PeekText, 10);
        Motion.Pop(PeekArtScale, 0.5, 1, 600, 0.7);
    }

    /// <summary>Opens the island as a pop-up card (notification, track, power, device) for <paramref name="ms"/>.</summary>
    private bool ShowCard(UIElement view, double w, double h, int ms)
    {
        if (Expanded || _countdown > 0) return false;
        if (_hudShown) { _hudTimer.Stop(); _hudShown = false; HudLeft.Visibility = HudRight.Visibility = Visibility.Collapsed; MiniLeft.Visibility = MiniRight.Visibility = Visibility.Visible; MiniLeft.Opacity = MiniRight.Opacity = 1; }
        if (_mode != Mode.Peek)
        {
            _mode = Mode.Peek;
            SetGlass(Mode.Peek);
            SetBlur(true);
            Motion.Hide(CollapsedView, 110);
            Motion.To(Rim, OpacityProperty, 1, 300);
            Motion.To(NotchShadow, OpacityProperty, 1, 300);
            Motion.To(LiveGlowHost, OpacityProperty, 0, 150);
            _liveGlowTarget = -1;
            Motion.To(NotchHost, OpacityProperty, 1, 150);
        }
        if (_card != view)
        {
            if (_card != null) Motion.Hide(_card, 110);
            view.Opacity = 0;
            Motion.Show(view, 300, 140, blur: 12);
            _card = view;
        }
        _targetW = _targetH = -1;
        Morph(w, h, PeekRadius, 540, 0.38);
        _peekTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _peekTimer.Stop();
        _peekTimer.Start();
        return true;
    }

    private void EndPeek()
    {
        if (_mode != Mode.Peek) return;
        _mode = Mode.Collapsed;
        SetGlass(Mode.Collapsed);
        if (_card != null) Motion.Hide(_card, 130, blur: 10);
        _card = null;
        Motion.To(Rim, OpacityProperty, 0, 200);
        Motion.To(NotchShadow, OpacityProperty, 0, 250);
        Motion.To(NotchGlow, OpacityProperty, 0, 250);
        CollapsedView.Opacity = 0;
        Motion.Show(CollapsedView, 260, 150, blur: 8);
        _targetW = _targetH = -1;
        ApplyCollapsed(animate: true);
    }

    /// <summary>The media tab grows taller when the lyrics stage is showing.</summary>
    private double ExpandedHeight => _tabIndex == 0 && LineBox.Visibility == Visibility.Visible && _media.Current != null
        ? ExpandedHLyrics : ExpandedH;

    private void ApplyCollapsed(bool animate)
    {
        if (_mode != Mode.Collapsed) return;
        var ticker = TickerContent();
        var showTicker = ticker != null;

        // lyrics get the karaoke highlight + a glowing note; captions / gestures stay plain
        TickerNote.Visibility = _tickerIsLyric ? Visibility.Visible : Visibility.Collapsed;
        TickerText.Foreground = _tickerIsLyric ? _karaoke : (Brush)FindResource("FgDim");
        if (showTicker && Ticker.Visibility != Visibility.Visible)
        {
            Ticker.Visibility = Visibility.Visible;
            Ticker.Opacity = 0;
            Motion.To(Ticker, OpacityProperty, 1, 260, Motion.Out, 120);
        }
        else if (!showTicker) Ticker.Visibility = Visibility.Collapsed;

        if (showTicker && ticker != _shownTicker)
        {
            _shownTicker = ticker!;
            TickerText.Text = ticker;
            Motion.SlideIn(TickerText, 8, 300);
        }
        if (!showTicker) _shownTicker = "";

        var glow = _media.Current?.IsPlaying == true && !showTicker ? 1.0 : 0.0;
        if (glow != _liveGlowTarget)
        {
            _liveGlowTarget = glow;
            Motion.To(LiveGlowHost, OpacityProperty, glow, 500);
        }

        var playing = _media.Current?.IsPlaying == true;
        if (playing != _eyesDancing) { _eyesDancing = playing; Eyes.SetDancing(playing); }

        var w = _hudShown ? Math.Max(HudW, showTicker ? CollapsedWTicker : 0)
              : showTicker ? CollapsedWTicker : MediaActive ? CollapsedWMedia : CollapsedW;
        var h = CollapsedH + (showTicker ? TickerH : 0);
        Morph(w, h, showTicker ? 22 : CollapsedRadius, animate ? 480 : 0, 0.3);
    }

    /// <summary>Fades the island out over fullscreen video; still opens on hover.</summary>
    private void UpdateFullscreenFade()
    {
        if (!_settings.HideInFullscreen || _mode != Mode.Collapsed || App.SnapshotDir != null) return;
        var fg = Native.GetForegroundWindow();
        var full = false;
        if (fg != IntPtr.Zero && fg != _hwnd && Native.GetWindowRect(fg, out var r))
        {
            var cls = new System.Text.StringBuilder(64);
            Native.GetClassName(fg, cls, cls.Capacity);
            var c = cls.ToString();
            if (c != "Progman" && c != "WorkerW")
            {
                var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                if (Native.GetMonitorInfo(Native.MonitorFromWindow(fg, 2), ref mi))
                    full = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                           r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
        }
        // 0.02 instead of 0 keeps the pixels hit-testable so hovering still opens it.
        var target = full && !Notch.IsMouseOver ? 0.02 : 1;
        if (Math.Abs(NotchHost.Opacity - target) > 0.01) Motion.To(NotchHost, OpacityProperty, target, 350);
        SetBlur(target > 0.5);   // no frosted rectangle floating over fullscreen video
    }

    // =======================================================================
    //  Clock & battery
    // =======================================================================
    private void UpdateClock()
    {
        var pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h') ? "h:mm" : "HH:mm";
        var now = DateTime.Now.ToString(pattern);
        MiniTime.Text = now;
        HeadTime.Text = now;
        HeadDate.Text = DateTime.Now.ToString("ddd d MMM");
    }

    private void UpdateBattery()
    {
        var (pct, charging, has) = PowerService.Battery();
        MiniBattery.Update(pct, charging, has);
        HeadBattery.Update(pct, charging, has);
        // the face gets drowsy when the battery is nearly empty
        Eyes.SetBaseMood(has && !charging && pct <= 15 ? EyeMood.Sleepy : EyeMood.Neutral);
        if (has && _lastCharging.HasValue && _lastCharging != charging)
        {
            Eyes.Express(charging ? EyeMood.Love : EyeMood.Surprised, 1800);
            Eyes.Jump(1.15);
        }
        if (has && _lastCharging.HasValue && _lastCharging != charging) ShowCharge(charging, pct);
        _lastCharging = has ? charging : null;
    }

    // =======================================================================
    //  Media
    // =======================================================================
    private bool MediaActive =>
        _media.Current is { } m && (m.IsPlaying || (DateTime.Now - _lastPlaying).TotalSeconds < 45);

    private void OnMediaChanged()
    {
        var m = _media.Current;
        var trackChanged = m?.TrackKey != _lastTrackKey;
        if (m?.IsPlaying == true) _lastPlaying = DateTime.Now;
        _theme.Set(m?.Accent ?? AccentTheme.Default);

        // collapsed pill
        MiniThumbBrush.ImageSource = m?.Thumbnail;
        MiniThumbImg.Visibility = m?.Thumbnail != null ? Visibility.Visible : Visibility.Collapsed;
        var active = MediaActive;
        if (active && MiniThumb.Visibility != Visibility.Visible)
        {
            MiniThumb.Visibility = Visibility.Visible;
            MiniEq.Visibility = Visibility.Visible;
            Motion.Pop(MiniThumbScale, 0.2, 1, 560, 0.7);
        }
        else if (!active)
        {
            MiniThumb.Visibility = Visibility.Collapsed;
            MiniEq.Visibility = Visibility.Collapsed;
        }
        else if (trackChanged) Motion.Pop(MiniThumbScale, 0.5, 1, 520, 0.7);

        var playing = m?.IsPlaying == true;
        MiniEq.IsPlaying = playing;
        HeadEq.IsPlaying = playing;
        HeadEq.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        if (_peekIsMedia) PeekEq.IsPlaying = playing;

        UpdateMediaPanel(trackChanged);

        // "Now playing" pop-up when a new track starts
        if (trackChanged && m != null && playing && !string.IsNullOrWhiteSpace(m.Title) && !Expanded)
            ShowPeek(m.Title, string.IsNullOrWhiteSpace(m.Artist) ? m.AppName : $"{m.Artist}  ·  {m.AppName}", m.Thumbnail, "", media: true);
        else if (_mode == Mode.Peek && _peekIsMedia && m != null && PeekArtBrush.ImageSource != m.Thumbnail)
        {
            PeekArtBrush.ImageSource = m.Thumbnail;
            PeekArt.Visibility = m.Thumbnail != null ? Visibility.Visible : Visibility.Collapsed;
            PeekArt.Opacity = 0;
            Motion.To(PeekArt, OpacityProperty, 1, 400);
        }

        _lastTrackKey = m?.TrackKey;
        _ = MaybeLoadLyricsAsync();
        ApplyCollapsed(animate: true);
    }

    private void UpdateMediaPanel(bool trackChanged)
    {
        var m = _media.Current;
        MediaEmpty.Visibility = m == null ? Visibility.Visible : Visibility.Collapsed;
        MediaContent.Visibility = m == null ? Visibility.Collapsed : Visibility.Visible;
        AmbientImage.Visibility = m?.Thumbnail != null ? Visibility.Visible : Visibility.Collapsed;
        if (m == null) return;

        TrackTitle.Text = string.IsNullOrWhiteSpace(m.Title) ? "Unknown title" : m.Title;
        TrackArtist.Text = string.IsNullOrWhiteSpace(m.Artist) ? m.AppName : m.Artist;
        AppName.Text = m.AppName;
        SourceMore.Visibility = _media.SessionApps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (BigThumbBrush.ImageSource != m.Thumbnail)
        {
            BigThumbBrush.ImageSource = m.Thumbnail;
            AmbientImage.Source = m.Thumbnail;
            BigThumb.Opacity = 0;
            Motion.To(BigThumb, OpacityProperty, 1, 450);
        }
        BigThumb.Visibility = m.Thumbnail != null ? Visibility.Visible : Visibility.Collapsed;

        var glyph = m.IsPlaying ? "" : "";
        if (PlayGlyph.Text != glyph)
        {
            PlayGlyph.Text = glyph;
            Motion.Pop(PlayGlyphScale, 0.3, 1, 420, 0.7);
        }

        // Artwork breathes: full size while playing, recedes when paused (like Apple Music).
        if (trackChanged && Expanded)
        {
            Motion.SlideIn(TrackInfo, 12, 380);
            Motion.Pop(ArtScale, 0.78, m.IsPlaying ? 1 : 0.9, 620, 0.55);
        }
        else if (_artPlaying != m.IsPlaying)
        {
            Motion.Scale(ArtScale, m.IsPlaying ? 1 : 0.9, 520, 0.45);
        }
        _artPlaying = m.IsPlaying;
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        var m = _media.Current;
        if (m == null || !Expanded) return;
        var dur = m.Duration;
        var pos = m.LivePosition;
        PosText.Text = Fmt(pos);
        DurText.Text = dur > TimeSpan.Zero ? Fmt(dur) : "";
        var frac = dur > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        var target = Math.Max(0, ProgTrack.ActualWidth * frac);
        // glide between ticks instead of stepping
        var step = TimeSpan.FromMilliseconds(Math.Abs(target - ProgFill.ActualWidth) > 40 ? 380 : 260);
        ProgFill.BeginAnimation(WidthProperty, new DoubleAnimation(target, step));
        ProgKnobX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Math.Max(0, target - 6), step));
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.PlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
    private void Source_Click(object sender, RoutedEventArgs e) => _media.CycleSource();

    private async void Progress_Click(object sender, MouseButtonEventArgs e)
    {
        var m = _media.Current;
        if (m == null || m.Duration <= TimeSpan.Zero) return;
        var frac = Math.Clamp(e.GetPosition(ProgTrack).X / ProgTrack.ActualWidth, 0, 1);
        await _media.SeekAsync(TimeSpan.FromSeconds(m.Duration.TotalSeconds * frac));
    }

    private void Progress_Enter(object sender, MouseEventArgs e)
    {
        Motion.To(ProgKnob, OpacityProperty, 1, 150);
        Motion.Scale(ProgKnobScale, 1, 360, 0.7);
        Motion.To(ProgTrackScale, ScaleTransform.ScaleYProperty, 1.6, 250, Motion.Spring(0.4));
    }

    private void Progress_Leave(object sender, MouseEventArgs e)
    {
        Motion.To(ProgKnob, OpacityProperty, 0, 200);
        Motion.Scale(ProgKnobScale, 0.4, 250, 0);
        Motion.To(ProgTrackScale, ScaleTransform.ScaleYProperty, 1, 250);
    }

    // =======================================================================
    //  Lyrics & captions (the "live line")
    // =======================================================================
    private async Task MaybeLoadLyricsAsync()
    {
        var m = _media.Current;
        if (m == null) { _currentLyrics = null; _lyricsKey = null; return; }
        var wanted = _settings.LyricsEnabled && (m.IsSpotify || LyricsToggle.IsChecked == true);
        if (!wanted) return;
        var key = m.Title + "|" + m.Artist;
        if (key == _lyricsKey) return;
        _lyricsKey = key;
        _currentLyrics = null;
        var result = await _lyrics.GetAsync(m.Title, m.Artist, m.Duration, fromMusicApp: m.IsSpotify);
        if (_lyricsKey == key) _currentLyrics = result;
        RefreshLiveLine();
    }

    /// <summary>Text under the collapsed pill. Gesture feedback wins, then captions, then lyrics.</summary>
    private string? TickerContent()
    {
        _tickerIsLyric = false;
        if (_hold is { } h)
            return h.gesture == "Closed_Fist"
                ? (_countdown > 0 ? "✊  Hold to cancel" : "✊  Hold to lock")
                : "🖐  Hold to restart";
        if (_settings.CaptionsEnabled && !string.IsNullOrWhiteSpace(_captionLine)) return _captionLine;
        if (LyricLine() is { Length: > 0 } l) { _tickerIsLyric = true; return l; }
        return null;
    }

    private string? LyricLine()
    {
        var m = _media.Current;
        if (!_settings.LyricsEnabled || _currentLyrics == null || m == null || !m.IsPlaying) return null;
        var idx = _currentLyrics.IndexAt(m.LivePosition);
        if (idx < 0) return null;
        var text = _currentLyrics.Lines[idx].Text;
        return text == "♪" ? null : text; // instrumental break → pill shrinks back
    }

    private void RefreshLiveLine()
    {
        var m = _media.Current;
        string prev = "", main = "", next = "";
        var isLyric = false;
        if (_settings.CaptionsEnabled)
        {
            main = string.IsNullOrWhiteSpace(_captionLine)
                ? (_captionStatus is "" or "Live" ? "Listening for speech…" : _captionStatus)
                : _captionLine;
            LineTag.Text = "LIVE CC";
        }
        else if (_settings.LyricsEnabled && m != null && _currentLyrics != null)
        {
            var lines = _currentLyrics.Lines;
            var pos = m.LivePosition;
            var idx = _currentLyrics.IndexAt(pos);
            prev = idx > 0 ? lines[idx - 1].Text : "";
            main = idx >= 0 ? lines[idx].Text : "♪   ♪   ♪";
            next = idx + 1 < lines.Count ? lines[idx + 1].Text : "";
            isLyric = true;
            LineTag.Text = "LYRICS";
            SyncKaraoke(idx, pos, m.IsPlaying);
        }
        else if (_settings.LyricsEnabled && m != null && (m.IsSpotify || LyricsToggle.IsChecked == true) && _lyricsKey != null)
        {
            main = "No synced lyrics for this track";
            LineTag.Text = "LYRICS";
        }

        if (isLyric != _lineIsLyric)
        {
            _lineIsLyric = isLyric;
            LineMain.Foreground = isLyric ? _karaoke : (Brush)FindResource("Fg");
            LineGlow.Visibility = isLyric ? Visibility.Visible : Visibility.Collapsed;
        }

        var show = main.Length > 0;
        if (show && LineBox.Visibility != Visibility.Visible)
        {
            LineBox.Opacity = 0;
            Motion.Show(LineBox, 320, 0, blur: 8, fromY: 10);
        }
        else if (!show) LineBox.Visibility = Visibility.Collapsed;

        if (main != _shownLine)
        {
            _shownLine = main;
            LineMain.Text = main;
            LineGlow.Text = main;
            LinePrev.Text = prev;
            LineNext.Text = next;
            if (show && Expanded)
            {
                // the whole stack scrolls up one line; the new line swells into focus
                Motion.SlideIn(LineStack, 20, 460);
                Motion.Pop(LineMainScale, 0.9, 1, 520, 0.5);
            }
        }
        LinePrev.Visibility = prev.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LineNext.Visibility = next.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (Expanded && _countdown == 0) Morph(ExpandedW, ExpandedHeight, ExpandedRadius, 460, 0.25);

        ApplyCollapsed(animate: true);
        if (_hold is { } h)
        {
            HoldTrack.Visibility = Visibility.Visible;
            var w = (HoldTrack.ActualWidth > 0 ? HoldTrack.ActualWidth : 330) * Math.Clamp(h.progress, 0, 1);
            HoldFill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(110)));
        }
        else
        {
            HoldTrack.Visibility = Visibility.Collapsed;
            HoldFill.BeginAnimation(WidthProperty, null);
            HoldFill.Width = 0;
        }
    }

    /// <summary>
    /// Sweeps the karaoke highlight across the current line in real time. One smooth animation per line,
    /// re-synced only when it drifts (seek, pause, skipped tick).
    /// </summary>
    private void SyncKaraoke(int idx, TimeSpan pos, bool playing)
    {
        if (_currentLyrics == null) return;
        double p = 0;
        TimeSpan end = pos;
        if (idx >= 0)
        {
            var start = _currentLyrics.Lines[idx].Time;
            end = _currentLyrics.LineEnd(idx);
            var dur = (end - start).TotalSeconds;
            p = dur <= 0 ? 1 : Math.Clamp((pos - start).TotalSeconds / dur, 0, 1);
        }

        var newLine = idx != _karaIdx;
        _karaIdx = idx;

        if (!playing || idx < 0 || end <= pos)
        {
            _kSung.BeginAnimation(GradientStop.OffsetProperty, null);
            _kEdge.BeginAnimation(GradientStop.OffsetProperty, null);
            _kSung.Offset = idx < 0 ? 0 : p;
            _kEdge.Offset = (idx < 0 ? 0 : p) + 0.04;
            _karaRunning = false;
            return;
        }

        if (newLine || !_karaRunning || Math.Abs(_kSung.Offset - p) > 0.08)
        {
            var remaining = end - pos;
            _kSung.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(p, 1, remaining));
            _kEdge.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(p + 0.04, 1.04, remaining));
            _karaRunning = true;
        }
    }

    private void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        _settings.LyricsEnabled = LyricsToggle.IsChecked == true;
        _settings.Save();
        _lyricsKey = null;
        if (!_settings.LyricsEnabled) _currentLyrics = null;
        _ = MaybeLoadLyricsAsync();
        RefreshLiveLine();
    }

    private void Cc_Click(object sender, RoutedEventArgs e)
    {
        _settings.CaptionsEnabled = CcToggle.IsChecked == true;
        _settings.Save();
        if (_settings.CaptionsEnabled) _captions.Start();
        else { _captions.Stop(); _captionLine = ""; _captionStatus = ""; }
        RefreshLiveLine();
    }

    // =======================================================================
    //  Tabs: sliding glass indicator + directional panel transitions
    // =======================================================================
    private FrameworkElement[] Panels => new FrameworkElement[] { MediaPanel, AppsPanel, ShelfPanel, PowerPanel };
    private RadioButton[] Tabs => new[] { TabMedia, TabApps, TabShelf, TabPower };

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PowerPanel == null) return; // fires during InitializeComponent
        var idx = int.Parse((string)((FrameworkElement)sender).Tag);
        var dir = idx >= _tabIndex ? 1 : -1;
        _tabIndex = idx;

        for (int i = 0; i < Panels.Length; i++)
            if (i != idx) Panels[i].Visibility = Visibility.Collapsed;

        var panel = Panels[idx];
        panel.Visibility = Visibility.Visible;
        panel.Opacity = 0;
        var t = new TranslateTransform(dir * 30, 0);
        panel.RenderTransform = t;
        Motion.To(panel, OpacityProperty, 1, 240);
        Motion.To(t, TranslateTransform.XProperty, 0, 460, Motion.Spring(0.35));

        if (idx == 1) RefreshApps(forceStagger: true);
        if (idx == 0) UpdateMediaPanel(false);
        if (idx == 2) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Stagger(ShelfList));
        // tab widths change (only the selected one shows its name) → move after layout
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => MoveTabIndicator(animate: true));
        if (Expanded) Morph(ExpandedW, ExpandedHeight, ExpandedRadius, 460, 0.25);
    }

    private void MoveTabIndicator(bool animate)
    {
        var rb = Tabs[_tabIndex];
        if (rb.ActualWidth <= 0) return;
        var x = rb.TranslatePoint(new Point(0, 0), TabStrip).X;
        if (animate)
        {
            Motion.To(TabIndicatorX, TranslateTransform.XProperty, x, 440, Motion.Spring(0.4));
            Motion.To(TabIndicator, WidthProperty, rb.ActualWidth, 440, Motion.Spring(0.4));
        }
        else
        {
            TabIndicatorX.BeginAnimation(TranslateTransform.XProperty, null);
            TabIndicatorX.X = x;
            TabIndicator.BeginAnimation(WidthProperty, null);
            TabIndicator.Width = rb.ActualWidth;
        }
    }

    private void StaggerCurrentPanel()
    {
        if (_tabIndex == 1) Stagger(AppsList);
        else if (_tabIndex == 2) Stagger(ShelfList);
    }

    /// <summary>Tiles cascade in one after another.</summary>
    private static void Stagger(ItemsControl list)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement c) continue;
            var delay = Math.Min(i, 14) * 30;
            c.Opacity = 0;
            var t = new TranslateTransform(0, 14);
            c.RenderTransform = t;
            Motion.To(c, OpacityProperty, 1, 280, Motion.Out, delay);
            Motion.To(t, TranslateTransform.YProperty, 0, 480, Motion.Spring(0.45), delay);
        }
    }

    // =======================================================================
    //  Apps
    // =======================================================================
    private void RefreshApps(bool forceStagger = false)
    {
        var changed = _apps.Refresh();
        AppsEmpty.Visibility = _apps.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changed || forceStagger) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Stagger(AppsList));
    }

    private void App_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is AppWindowItem item)
        {
            AppsService.Focus(item.Hwnd);
            Collapse();
        }
    }

    private async void AppClose_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is AppWindowItem item && sender is DependencyObject d)
        {
            // shrink the tile away before the window closes
            if (FindParent<ContentPresenter>(d) is { } tile)
            {
                var s = new ScaleTransform(1, 1, tile.ActualWidth / 2, tile.ActualHeight / 2);
                tile.RenderTransform = s;
                Motion.Scale(s, 0.6, 220, 0);
                Motion.To(tile, OpacityProperty, 0, 200);
            }
            AppsService.Close(item.Hwnd);
            await Task.Delay(500);
            RefreshApps();
        }
    }

    // =======================================================================
    //  Shelf (drag & drop)
    // =======================================================================
    private void UpdateShelfState()
    {
        var any = _shelf.Items.Count > 0;
        ShelfEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ShelfFilled.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool CanAccept(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText);

    private void Notch_DragEnter(object sender, DragEventArgs e)
    {
        if (_draggingOut) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        _collapseTimer.Stop();
        Expand();
        TabShelf.IsChecked = true;
        if (CanAccept(e) && DropOverlay.Visibility != Visibility.Visible)
        {
            DropOverlay.Opacity = 0;
            Motion.Show(DropOverlay, 200, 0, blur: 0);
        }
        e.Effects = CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Notch_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && CanAccept(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Notch_DragLeave(object sender, DragEventArgs e)
    {
        Motion.Hide(DropOverlay, 160, blur: 0);
        RestartCollapseTimer();
    }

    private void Notch_Drop(object sender, DragEventArgs e)
    {
        Motion.Hide(DropOverlay, 160, blur: 0);
        if (_draggingOut) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _shelf.AddPaths(files);
        else if (e.Data.GetData(DataFormats.UnicodeText) is string text) _shelf.AddText(text);
        e.Handled = true;

        // the new tile "lands" with a bounce
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ShelfList.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement c)
            {
                var s = new ScaleTransform(0.3, 0.3, c.ActualWidth / 2, c.ActualHeight / 2);
                c.RenderTransform = s;
                Motion.Pop(s, 0.3, 1, 600, 0.8);
            }
        });
        RestartCollapseTimer();
    }

    private void ShelfItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ShelfItem item) return;
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return; // remove button
        if (e.ClickCount == 2)
        {
            ShelfService.Open(item);
            _dragItem = null;
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragItem = item;
    }

    private void ShelfItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < 5 && Math.Abs(p.Y - _dragStart.Y) < 5) return;

        var item = _dragItem;
        _dragItem = null;
        if (!item.Exists) return;
        _draggingOut = true;
        try
        {
            var data = item.IsText
                ? new DataObject(DataFormats.UnicodeText, item.Text)
                : new DataObject(DataFormats.FileDrop, new[] { item.Path });
            var result = DragDrop.DoDragDrop((DependencyObject)sender, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result == DragDropEffects.Move && !item.Exists) _shelf.Remove(item);
        }
        finally
        {
            _draggingOut = false;
            if (!Notch.IsMouseOver) RestartCollapseTimer();
        }
    }

    private void ShelfItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ShelfItem item) ShelfService.Reveal(item);
    }

    private void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ShelfItem item) _shelf.Remove(item);
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => _shelf.Clear();

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // =======================================================================
    //  Power controls + countdown
    // =======================================================================
    private void Lock_Click(object sender, RoutedEventArgs e) { Collapse(); PowerService.Lock(); }
    private void Sleep_Click(object sender, RoutedEventArgs e) { Collapse(); PowerService.Sleep(); }
    private void Restart_Click(object sender, RoutedEventArgs e) => StartCountdown("Restarting", "Restart now", "Close your hand ✊ or press Cancel to stop", PowerService.Restart);
    private void Shutdown_Click(object sender, RoutedEventArgs e) => StartCountdown("Shutting down", "Shut down now", "Close your hand ✊ or press Cancel to stop", PowerService.Shutdown);

    private const double RingLength = 51.8; // circumference of the ring in stroke-thickness units

    private void StartCountdown(string verb, string nowText, string sub, Action action)
    {
        if (_countdown > 0) return;
        _countdown = Math.Max(1, _settings.PowerCountdownSec);
        _countdownAction = action;
        CountdownTitle.Tag = verb;
        CountdownNowText.Text = nowText;
        CountdownSub.Text = sub;
        Expand();

        CountdownOverlay.Opacity = 0;
        Motion.Show(CountdownOverlay, 300, 0, blur: 14);
        // the ring drains smoothly over the whole countdown
        CountdownRing.StrokeDashArray = new DoubleCollection { RingLength, 400 };
        CountdownRing.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(0, RingLength, TimeSpan.FromSeconds(_countdown)));
        UpdateCountdownText();
        _countdownTimer.Start();
    }

    private void UpdateCountdownText()
    {
        CountdownNumber.Text = _countdown.ToString();
        CountdownTitle.Text = $"{CountdownTitle.Tag} in {_countdown} s";
        Motion.Pop(CountdownNumberScale, 1.6, 1, 480, 0.5);
    }

    private void OnCountdownTick()
    {
        _countdown--;
        if (_countdown <= 0) FireCountdown();
        else UpdateCountdownText();
    }

    private void FireCountdown()
    {
        _countdownTimer.Stop();
        var action = _countdownAction;
        _countdown = 0;
        _countdownAction = null;
        Motion.Hide(CountdownOverlay, 150);
        try { action?.Invoke(); }
        catch (Exception ex) { App.Log(ex); }
    }

    private void CancelCountdown()
    {
        _countdownTimer.Stop();
        _countdown = 0;
        _countdownAction = null;
        CountdownRing.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        Motion.Hide(CountdownOverlay, 220, blur: 12);
        if (!Notch.IsMouseOver) RestartCollapseTimer();
    }

    private void CountdownCancel_Click(object sender, RoutedEventArgs e) => CancelCountdown();
    private void CountdownNow_Click(object sender, RoutedEventArgs e) => FireCountdown();

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        try { PowerService.StartsWithWindows = StartupSwitch.IsChecked == true; }
        catch (Exception ex) { App.Log(ex); StartupSwitch.IsChecked = PowerService.StartsWithWindows; }
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        // the island retracts into the top edge, then the app exits
        var t = NotchHost.RenderTransform as TranslateTransform ?? new TranslateTransform();
        NotchHost.RenderTransform = t;
        Motion.Hide(ExpandedView, 120);
        Morph(CollapsedW, CollapsedH, CollapsedRadius, 300, 0);
        Motion.To(t, TranslateTransform.YProperty, -60, 380, new CubicEase { EasingMode = EasingMode.EaseIn }, 220, done: () =>
        {
            Cleanup();
            Application.Current.Shutdown();
        });
    }

    // =======================================================================
    //  Camera gestures
    // =======================================================================
    private async Task StartGesturesAsync()
    {
        if (_gestures == null)
        {
            // Prefer the Python/OpenCV engine (cleaner camera capture + picture clean-up);
            // fall back to the built-in WebView2 engine when Python/OpenCV/MediaPipe aren't installed.
            string? python = null;
            if (_settings.GestureEngine != "web")
            {
                _gestureState = "loading"; UpdateGestureUi();
                python = await PythonGestureHost.FindPythonAsync(_settings.PythonPath);
            }
            if (_gestures != null) return; // started meanwhile
            App.Log(python != null ? $"gestures: using Python engine ({python})" : "gestures: using built-in WebView2 engine");
            _gestures = python != null ? new PythonGestureHost(python) : new GestureHost();
            _gestures.MinHandSize = _settings.MinHandSize;
            _gestures.StatusChanged += st => { App.Log("gestures: " + st); _gestureState = st; UpdateGestureUi(); };
            _gestures.Frame += OnGestureFrame;
            _gestures.PreviewClosed += () =>
            {
                _settings.ShowCameraPreview = false;
                _settings.Save();
                PreviewChip.IsChecked = false;
            };
        }
        _gestures.SetPreview(_settings.ShowCameraPreview);
        await _gestures.StartAsync();
    }

    private void UpdateGestureUi()
    {
        var watching = _gestureState == "watching";
        MiniCamDot.Visibility = watching ? Visibility.Visible : Visibility.Collapsed;
        HeadCamDot.Visibility = MiniCamDot.Visibility;
        GestureStatus.Text = _gestureState switch
        {
            "watching" => "Watching  ·  ✊ lock  ·  🖐 restart",
            "loading" or "ready" => "Loading hand-tracking model…",
            "off" or "" => _settings.GesturesEnabled ? "Paused" : "Off  ·  ✊ lock  ·  🖐 restart",
            _ when _gestureState.StartsWith("error") => _gestureState.Replace("error: ", "⚠ "),
            _ => _gestureState,
        };
    }

    private async void GestureSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.GesturesEnabled = GestureSwitch.IsChecked == true;
        _settings.Save();
        if (_settings.GesturesEnabled) await StartGesturesAsync();
        else { _gestures?.Stop(); SetHold(null); }
        UpdateGestureUi();
    }

    private async void PreviewChip_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowCameraPreview = PreviewChip.IsChecked == true;
        _settings.Save();
        if (_settings.ShowCameraPreview && !_settings.GesturesEnabled)
        {
            // preview needs the camera running → switch gestures on as well
            _settings.GesturesEnabled = true;
            _settings.Save();
            GestureSwitch.IsChecked = true;
            await StartGesturesAsync();
            UpdateGestureUi();
        }
        else _gestures?.SetPreview(_settings.ShowCameraPreview);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // Release the camera while the PC is locked (Windows Hello may need it, and
        // no accidental restart can be triggered from the lock screen).
        if (e.Reason == SessionSwitchReason.SessionLock) Native.LockedByEvent = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) Native.LockedByEvent = false;
        App.Log("session: " + e.Reason);
        Dispatcher.BeginInvoke(async () =>
        {
            if (e.Reason == SessionSwitchReason.SessionLock) { _gestures?.Stop(); SetHold(null); }
            else if (e.Reason == SessionSwitchReason.SessionUnlock && _settings.GesturesEnabled)
            {
                _gCooldown = DateTime.UtcNow.AddSeconds(3); // don't instantly re-lock after unlocking
                await StartGesturesAsync();
            }
        });
    }

    private void OnGestureFrame(string gesture, double score, double size)
    {
        var now = DateTime.UtcNow;
        _gFrames++;
        if (gesture != "None" && now >= _gNextTrace)
        {
            _gNextTrace = now.AddSeconds(2);
            App.Log($"hand seen: {gesture} score {score:0.00} size {size:0.00} (need ≥ {_settings.MinHandSize:0.00})");
        }
        var near = size >= _settings.MinHandSize && score >= 0.55;
        var eff = near && gesture is "Closed_Fist" or "Open_Palm" ? gesture : "None";

        if (eff != _gCur) { _gCur = eff; _gSince = now; }
        if (eff == "None" || now < _gCooldown || (eff == "Open_Palm" && _countdown > 0))
        {
            SetHold(null);
            return;
        }

        var progress = (now - _gSince).TotalMilliseconds / _settings.HoldMs;
        if (progress < 1) { SetHold((eff, progress)); return; }

        _gCooldown = now.AddSeconds(4);
        _gCur = "None";
        SetHold(null);

        // Safety net: never act from the lock screen / secure desktop.
        if (Native.IsSessionLocked()) { _gestures?.Stop(); return; }
        App.Log($"gesture fired: {eff} (score {score:0.00}, hand size {size:0.00})");

        if (eff == "Closed_Fist")
        {
            if (_countdown > 0) CancelCountdown();  // fist during a countdown = cancel
            else PowerService.Lock();
        }
        else if (eff == "Open_Palm")
        {
            StartCountdown("Restarting", "Restart now", "Open palm 🖐 detected — close your hand ✊ or press Cancel to stop", PowerService.Restart);
        }
    }

    private void SetHold((string gesture, double progress)? hold)
    {
        if (hold == null && _hold == null) return;
        _hold = hold;
        RefreshLiveLine();
    }
}
