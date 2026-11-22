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
    private const double ExpandedW = 580, ExpandedH = 264, ExpandedHLyrics = 304, ExpandedHChat = 330, ExpandedRadius = 34;
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
    // chats + music search
    private readonly ChatService _chats = new();
    private ChatThread? _openThread, _pendingChat;
    private readonly System.Collections.ObjectModel.ObservableCollection<YtResult> _results = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private string _resultsFor = "";
    private readonly YouTubePlayer _player = new();
    private readonly ActivityService _activity = new();
    private string? _activityLine;
    private ActivityItem? _live;
    private bool _liveShown;
    private bool? _lastLowWarn;
    private const double CollapsedWLive = 312;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private bool _typing;
    private readonly DeviceWatcher _devices = new();
    private UIElement? _card;                                  // view shown in Peek mode (peek / charge / device)
    // the island's tint over the acrylic blur: solid while collapsed, frosted when open
    private readonly SolidColorBrush _glass = new(Color.FromRgb(0, 0, 0));
    private static readonly Color GlassCollapsed = Color.FromArgb(0xF2, 0x1C, 0x1C, 0x22);   // graphite, not black
    // smoked glass (no system blur behind it, so it stays nearly opaque to keep text readable)
    private static readonly Color GlassPeek = Color.FromArgb(0xF5, 0x0C, 0x0C, 0x10);
    private static readonly Color GlassOpen = Color.FromArgb(0xF0, 0x0E, 0x0E, 0x12);
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

    // clap control (microphone)
    private ClapListener? _claps;
    private string _clapState = "off";
    private DateTime _clapCooldown;

    // shelf drag-out tracking
    private Point _dragStart;
    private ShelfItem? _dragItem;

    // animatable corner radius of the island (drives CornerRadius + clip)
    public static readonly DependencyProperty NotchRadiusProperty = DependencyProperty.Register(
        nameof(NotchRadius), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(CollapsedRadius, (d, _) => ((NotchWindow)d).ApplyShape()));
    public double NotchRadius { get => (double)GetValue(NotchRadiusProperty); set => SetValue(NotchRadiusProperty, value); }

    // top corners: square while the island hugs the screen edge, rounded once it floats free (open)
    public static readonly DependencyProperty NotchTopRadiusProperty = DependencyProperty.Register(
        nameof(NotchTopRadius), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(0.0, (d, _) => ((NotchWindow)d).ApplyShape()));
    public double NotchTopRadius { get => (double)GetValue(NotchTopRadiusProperty); set => SetValue(NotchTopRadiusProperty, value); }

    private readonly TranslateTransform _hostY = new();
    private const double FloatGap = 10;     // how far the open island drops away from the top edge

    public NotchWindow()
    {
        InitializeComponent();
        _theme = new AccentTheme(Resources); // DynamicResource picks the live brushes up from here

        foreach (var fx in new[] { NotchGlowFx, LiveGlowFx, HoldGlowFx, PeekGlowFx, ArtGlowFx, ProgGlowFx, DropGlowFx, HudGlowFx, HudBarGlowFx, Eyes.Glow })
            _theme.RegisterGlow(fx);
        _hudTimer.Tick += (_, _) => { _hudTimer.Stop(); HideHud(); };
        Notch.Background = _glass;
        NotchHost.RenderTransform = _hostY;
        NotchShadow.RenderTransform = _hostY;
        NotchGlow.RenderTransform = _hostY;
        EarL.Fill = _glass;
        EarR.Fill = _glass;
        _theme.Changed += (c, ms) => Eyes.SetIris(c, ms);
        Eyes.Interacted += () => _hoverIntent.Stop();
        Eyes.Reaction += OnEyesReaction;
        _bubbleTimer.Tick += (_, _) => { if (!_voice.Speaking) HideBubble(); };
        InitVoice();
        InitLive();
        _notifTick.Tick += (_, _) => OnNotifTick();
        TalkSwitch.IsChecked = _settings.EyesTalk;
        StandbySwitch.IsChecked = _settings.StandbyAfterMinutes > 0;
        LivePillSwitch.IsChecked = _settings.LiveInPill;
        NotifsList.ItemsSource = _history;
        BannerListItems.ItemsSource = _listItems;
        TodoList.ItemsSource = _todo.Items;
        _listClose.Tick += (_, _) => CloseBannerList();
        UpdateNotifsState();
        UpdateTodoState();

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
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_typing) return;               // stay open while you're typing a reply / search
            if (!Notch.IsMouseOver) Collapse();
        };
        ChatsList.ItemsSource = _chats.Threads;
        ResultsList.ItemsSource = _results;
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await SearchAsync(SearchBox.Text); };
        _media.External = _player;
        ActivityList.ItemsSource = _activity.Items;
        _activity.StateChanged += OnActivityChanged;
        _activity.Items.CollectionChanged += (_, _) => ActivityEmpty.Visibility = _activity.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Deactivated += (_, _) => { StopTyping(); if (Expanded && !Notch.IsMouseOver) RestartCollapseTimer(); };
        _hoverIntent.Tick += (_, _) =>
        {
            _hoverIntent.Stop();
            if (!Notch.IsMouseOver && !TopHotZone.IsMouseOver) return;
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
        ClapChip.IsChecked = _settings.ClapsEnabled;
        BreakSwitch.IsChecked = _settings.BreaksEnabled;
        UpdateBreakUi();
        StartupSwitch.IsChecked = PowerService.StartsWithWindows;
        InitUpdates();

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
        if (App.SnapshotDir == null) RegisterPrivacyHotkey();
        PositionWindow();
        // System blur is off: on this Windows build DWM draws accent / system backdrops as a solid square
        // that ignores the window's rounded clip region, so the island is drawn entirely by WPF instead
        // (smooth anti-aliased corners, dark smoked glass).
        if (App.SnapshotDir == null && _settings.SystemBlur)
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

        // Entrance: a droplet falls, bounces like a ball, then melts into the top edge and spreads into the island.
        if (App.SnapshotDir == null || App.IntroCapture) PlayIntro();
        else EnterFloatingPill(0);   // snapshots skip the intro: start straight on the floating pill
        if (App.IntroCapture) { _ = CaptureIntroAsync(App.SnapshotDir!); return; }

        _media.Changed += OnMediaChanged;
        try { await _media.InitAsync(); }
        catch (Exception ex) { App.Log("media init: " + ex); }

        _captions.LineChanged += line => Dispatcher.BeginInvoke(() => { _captionLine = line; RefreshLiveLine(); });
        _captions.StatusChanged += st => Dispatcher.BeginInvoke(() => { _captionStatus = st; RefreshLiveLine(); });
        if (_settings.CaptionsEnabled) _captions.Start();

        if (App.SnapshotDir != null) { _clock.Start(); await RunSnapshotsAsync(App.SnapshotDir); return; }

        StartSystemSignals();

        // QA hooks: --demo expand | standby  (opens that view a few seconds after start)
        var args = Environment.GetCommandLineArgs();
        // QA hook: --say "text"  (the eyes say it, out loud when the voice is on)
        var sayAt = Array.IndexOf(args, "--say");
        if (sayAt >= 0 && sayAt + 1 < args.Length) SayLater(args[sayAt + 1], 6000, 3000);
        // QA hook: --talk  (starts a live conversation a few seconds after launch)
        var talkAt = Array.IndexOf(args, "--talk");
        if (talkAt >= 0 && talkAt + 1 < args.Length && !args[talkAt + 1].StartsWith("--")) _talk.FirstMessage = args[talkAt + 1];
        if (args.Contains("--talk")) { var tt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) }; tt.Tick += (_, _) => { tt.Stop(); ToggleLiveTalk(); }; tt.Start(); }
        var demo = Array.IndexOf(args, "--demo");
        if (demo >= 0 && demo + 1 < args.Length)
        {
            var what = args[demo + 1];
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
            t.Tick += async (_, _) =>
            {
                t.Stop();
                if (what == "expand") Expand();
                else if (what == "standby") ShowStandby();
            };
            t.Start();
        }

        // If we start while the PC is already locked, wait for the unlock event instead.
        if (_settings.ClapsEnabled && !Native.IsSessionLocked()) StartClaps();
        SyncWakeWord();
        if (_settings.GesturesEnabled && !Native.IsSessionLocked()) await StartGesturesAsync();
        UpdateGestureUi();

        _clock.Start();
        OnMediaChanged();
    }

    // =======================================================================
    //  Launch intro: droplet → bounce → merge into the top edge → island
    // =======================================================================
    private bool _intro;

    private void PlayIntro()
    {
        _intro = true;
        const double D = 20;            // droplet diameter
        const double Floor = 44;        // where it lands (top of the drop, in px from the screen edge)

        var drop = new Border
        {
            Width = D, Height = D, CornerRadius = new CornerRadius(D / 2), Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 4, Direction = 270, Opacity = 0.45 },
        };
        // glossy highlight so it reads as a liquid drop
        drop.Child = new Ellipse
        {
            Width = 7, Height = 4.5, Fill = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 3.4, 0, 0), RenderTransform = new RotateTransform(-20),
        };
        var squash = new ScaleTransform(1, 1);
        var move = new TranslateTransform(0, -D - 8);
        drop.RenderTransform = new TransformGroup { Children = { squash, move } };
        Root.Children.Add(drop);
        NotchHost.Opacity = 0;

        static EasingDoubleKeyFrame K(double v, int ms, IEasingFunction? e = null) =>
            new(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), e);
        var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var suck = new CubicEase { EasingMode = EasingMode.EaseIn };

        // position: fall → bounce → smaller bounce → settle → pulled up into the edge
        var y = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                K(-D - 8, 0), K(Floor, 430, easeIn),
                K(Floor - 26, 650, easeOut), K(Floor, 860, easeIn),
                K(Floor - 9, 975, easeOut), K(Floor, 1085, easeIn),
                K(Floor, 1230),
                K(-2, 1500, suck),
            },
        };
        // squash & stretch: long while falling, flat on impact, tall when sucked up
        var sx = new DoubleAnimationUsingKeyFrames();
        var sy = new DoubleAnimationUsingKeyFrames();
        void S(double x, double yy, int ms, IEasingFunction? e = null) { sx.KeyFrames.Add(K(x, ms, e)); sy.KeyFrames.Add(K(yy, ms, e)); }
        var springy = new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut };
        S(0.9, 1.15, 0);
        S(0.84, 1.22, 400);                       // stretched just before the first impact
        S(1.42, 0.62, 440);                       // splat
        S(1, 1, 560, springy);
        S(0.92, 1.1, 840);
        S(1.25, 0.78, 868);                       // second, softer splat
        S(1, 1, 960, springy);
        S(1.1, 0.9, 1092);                        // tiny tap
        S(1, 1, 1180, springy);
        S(1.12, 0.88, 1260);                      // anticipation before being pulled up
        S(0.74, 1.45, 1500, suck);                // stretched thin as it shoots up

        sx.Completed += (_, _) => MergeIntoIsland(drop);
        move.BeginAnimation(TranslateTransform.YProperty, y);
        squash.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        squash.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    private void MergeIntoIsland(UIElement drop)
    {
        Root.Children.Remove(drop);
        const double D = 20;

        // the island starts as the drop hanging from the edge, then spreads out like liquid
        foreach (var a in new[] { WidthProperty, HeightProperty }) Notch.BeginAnimation(a, null);
        BeginAnimation(NotchRadiusProperty, null);
        Notch.Width = D;
        Notch.Height = D + 4;
        NotchRadius = D / 2;
        NotchHost.BeginAnimation(OpacityProperty, null);
        NotchHost.Opacity = 1;
        CollapsedView.Opacity = 0;

        // concave "ears" grow in as it touches the edge
        foreach (var (ear, origin) in new[] { (EarL, new Point(1, 0)), (EarR, new Point(0, 0)) })
        {
            var s = new ScaleTransform(0, 0);
            ear.RenderTransformOrigin = origin;
            ear.RenderTransform = s;
            Motion.Scale(s, 1, 520, 0.5);
        }

        _intro = false;
        _targetW = _targetH = -1;
        var playing = MediaActive;
        var w = playing ? CollapsedWMedia : CollapsedW;
        // wobbly spread: width overshoots and settles, height follows a beat later
        Notch.BeginAnimation(WidthProperty, new DoubleAnimation(D, w, TimeSpan.FromMilliseconds(900))
        { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 9, EasingMode = EasingMode.EaseOut } });
        Notch.BeginAnimation(HeightProperty, new DoubleAnimation(D + 4, CollapsedH, TimeSpan.FromMilliseconds(520))
        { BeginTime = TimeSpan.FromMilliseconds(60), EasingFunction = Motion.Spring(0.6) });
        Motion.To(this, NotchRadiusProperty, CollapsedRadius, 500, Motion.Out);
        _targetW = w;
        _targetH = CollapsedH;

        // content fades in once it has spread, and the eyes wake up
        Motion.Show(CollapsedView, 420, 360, blur: 10);
        var wake = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(620) };
        wake.Tick += (_, _) =>
        {
            wake.Stop();
            SayLater(Greeting(), 900, 3000);
            Eyes.Jump(1.2);
            Eyes.Blink(twice: true);
            ApplyCollapsed(animate: true);
            EnterFloatingPill(520);   // let go of the edge and float as a frosted capsule
        };
        wake.Start();
    }

    /// <summary>QA: saves the top of the window every 60 ms while the intro plays, then exits.</summary>
    private async Task CaptureIntroAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var dpi = VisualTreeHelper.GetDpi(this);
        for (int i = 0; i < 42; i++)
        {
            UpdateLayout();
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(110 * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xEF)), null, new Rect(0, 0, ActualWidth, 110));
            rtb.Render(dv);
            rtb.Render(Root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, $"intro-{i:00}.png"))) enc.Save(fs);
            await Task.Delay(60);
        }
        Application.Current.Shutdown();
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
            Say("Hello there! 👋", 60000); await Task.Delay(900); Save("1k-bubble"); HideBubble(); await Task.Delay(300);
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
            var anaChat = _chats.Add("WhatsApp", new ToastInfo { Title = "Ana", Body = "Are you coming tonight? We're at the usual place 🍕" }, null); ShowNotif(new NotifData("WhatsApp", "Ana", "Are you coming tonight? We're at the usual place 🍕", null, "\uE8BD", anaChat, Color.FromRgb(0x25, 0xD3, 0x66))); ShowNotif(new NotifData("Mail", "Weekly report", "Your summary is ready", null, "\uE715", null));
            await Task.Delay(1200); Save("1e-notification");
            ShowNotif(new NotifData("Slack", "#design", "Mia: new mockups for the island are up 👀", null, "", null));
            ShowNotif(new NotifData("Calendar", "Stand-up in 10 min", "Room 4 · Google Meet", null, "", null));
            await Task.Delay(500); Save("1e2-notif-stack");
            BannerViewAll_Click(BannerMore, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            await Task.Delay(180); Save("1e3-viewall-unfold");
            await Task.Delay(1200); Save("1e4-viewall");
            CloseBannerList();
            await Task.Delay(700);
            var demoTodo = new TodoItem { Text = "Call mom", Due = DateTime.Now };
            ShowNotif(new NotifData("Reminder", demoTodo.Text, $"Due {DateTime.Now:HH:mm}  ·  from your to-do list", null, "", null, Color.FromRgb(0xFF, 0x9F, 0x0A), demoTodo));
            await Task.Delay(1300); Save("1e5-reminder");
            CloseBanner(instant: true);
            _notifQueue.Clear();
            await Task.Delay(400);
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
            // chats + music search
            _chats.Add("Instagram", new ToastInfo { Title = "travel.with.mia", Body = "sent you a reel 🎬" }, null);
            _chats.Add("WhatsApp", new ToastInfo { Title = "Ana", Body = "Hey, are you coming tonight?" }, null);
            _chats.Add("WhatsApp", new ToastInfo { Title = "Ana", Body = "We're at the usual place 🍕" }, null);
            UpdateChatsState();
            TabChats.IsChecked = true;
            await Task.Delay(1100); Save("10-chats");
            OpenThread(_chats.Threads[0]);
            _chats.Threads[0].Messages.Add(new ChatMessage { From = "You", Text = "On my way! 10 min 🚀", Mine = true });
            await Task.Delay(900); Save("11-chat-reply");
            TabSearch.IsChecked = true;
            SearchBox.Text = "indila";
            await SearchAsync("indila");
            await Task.Delay(2500); Save("12-search");
            _activity.Items.Clear();
            _activity.Items.Add(new ActivityItem { Key = "s1", App = "Claude Code", Kind = "session", Title = "Windows Notch",
                Detail = "Building the live-activity animations: orbiting ring, light sweep and a ticking timer…", State = ActivityState.Working, StartedAt = DateTime.Now.AddSeconds(-84) });
            _activity.Items.Add(new ActivityItem { Key = "t1", App = "Terminal", Kind = "terminal", Title = "terminal",
                Detail = "Running  dotnet publish -c Release -r win-x64", State = ActivityState.Working, StartedAt = DateTime.Now.AddSeconds(-23) });
            _activity.Items.Add(new ActivityItem { Key = "a1", App = "Antigravity", Kind = "app", Title = "landing-page  ·  App.tsx",
                Detail = "Finished working", State = ActivityState.Done });
            _activity.Items.Add(new ActivityItem { Key = "c1", App = "ChatGPT", Kind = "app", Title = "Open", Detail = "Reply finished", State = ActivityState.Idle });
            UpdateLive();
            TabActivity.IsChecked = true;
            await Task.Delay(1500); Save("13-activity");
            TabNotifs.IsChecked = true;
            await Task.Delay(1200); Save("16-alerts");
            // to-do: shown from in-memory items only (snapshot never writes your todo.json)
            var keep = _todo.Items.ToList();
            _todo.Items.Clear();
            _todo.Items.Add(new TodoItem { Text = "Send the invoice to Daniel", Due = DateTime.Now.AddMinutes(-5) });
            _todo.Items.Add(new TodoItem { Text = "Stretch break", Due = DateTime.Now.AddMinutes(25) });
            _todo.Items.Add(new TodoItem { Text = "Buy groceries", Due = DateTime.Today.AddDays(1).AddHours(9) });
            _todo.Items.Add(new TodoItem { Text = "Push the notch release" });
            _todo.Items.Add(new TodoItem { Text = "Reply to Ana", Done = true });
            UpdateTodoState();
            TabTodo.IsChecked = true;
            TodoBox.Text = "call mom at 5pm";
            await Task.Delay(1200); Save("17-todo");
            TodoBox.Text = "";
            _todo.Items.Clear(); foreach (var k in keep) _todo.Items.Add(k);
            _settings.LiveInPill = true; UpdateLive();
            Collapse();
            await Task.Delay(1600); Save("14-live-pill");
            _settings.LiveInPill = false;
            Expand();
            await Task.Delay(600);
            {
                var preview = new StandbyWindow(_media, false, () => _currentLyrics).RenderPreview();
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
                using var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "15-standby.png"));
                enc.Save(fs);
            }
            {
                // the lock screen with synced lyrics (a pretend song and made-up lines, whatever is playing on this PC)
                var keepLyrics = _currentLyrics;
                _media.SetForSnapshot(new MediaInfo { AppId = "Spotify.exe", AppName = "Spotify", Title = "Midnight Drive", Artist = "Demo Artist",
                    IsPlaying = true, Position = TimeSpan.FromSeconds(31), Duration = TimeSpan.FromSeconds(200), LastUpdated = DateTimeOffset.Now,
                    Accent = Color.FromRgb(0xC0, 0x2A, 0x4A) });
                var ly = new LyricsResult();
                ly.Lines.Add(new LyricLine(TimeSpan.FromSeconds(20), "Streetlights humming in the rain"));
                ly.Lines.Add(new LyricLine(TimeSpan.FromSeconds(26), "We keep driving through the night"));
                ly.Lines.Add(new LyricLine(TimeSpan.FromSeconds(30), "Every song is playing just for you and me"));
                ly.Lines.Add(new LyricLine(TimeSpan.FromSeconds(36), "Till the morning comes around"));
                _currentLyrics = ly;
                var preview2 = new StandbyWindow(_media, false, () => _currentLyrics).RenderPreview();
                var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview2));
                using (var fs2 = System.IO.File.Create(System.IO.Path.Combine(dir, "15b-standby-lyrics.png"))) enc2.Save(fs2);
                // the break screen
                var brk = new StandbyWindow(_media, true, null, DateTime.Now.AddMinutes(4).AddSeconds(32)).RenderPreview();
                var enc3 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc3.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(brk));
                using (var fs3 = System.IO.File.Create(System.IO.Path.Combine(dir, "15c-break.png"))) enc3.Save(fs3);
                _currentLyrics = keepLyrics;
            }
            StartCountdown("Restarting", "Restart now", CancelHint(), () => { });
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
        _claps?.Dispose();
        _wake?.Dispose();
        _voice.Stop();
        _talk.Stop("quit");
        _volume.Dispose();
        _brightness.Dispose();
        _notifications.Dispose();
        _devices.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _backing?.Dispose();
        _player.Dispose();
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
            _notifications.Received += t => Dispatcher.BeginInvoke(() => OnToast(t));
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

    /// <summary>A Windows toast arrived: chat messages go to the Chats tab (ready to reply), everything pops up.</summary>
    private void OnToast(ToastInfo t)
    {
        _activity.OnToast(t.App, t.Title, t.Body);
        var messenger = ChatService.MessengerOf(t);
        if (messenger != null)
        {
            var thread = _chats.Add(messenger, t, AppIconFor(t.App) ?? AppIconFor(messenger));
            _pendingChat = thread;
            UpdateChatsState();
            if (Expanded && _tabIndex == 4 && _openThread == thread) ScrollChatToEnd();
        }
        OnNotification(messenger ?? t.App, t.Title, t.Body, isChat: messenger != null);
    }

    private void OnNotification(string app, string title, string body, bool isChat = false)
    {
        var head = string.IsNullOrWhiteSpace(title) ? app : title;
        var sub = string.IsNullOrWhiteSpace(body) || body == head ? "" : body;
        var icon = AppIconFor(app);
        var chat = isChat ? _pendingChat : null;
        _pendingChat = null;   // only set again when you click the banner / Reply
        if (Expanded || _countdown > 0)
        {
            // no banner while the notch is open — still keep it in the Alerts tab
            AddHistory(new NotifData(string.IsNullOrWhiteSpace(app) ? "Notification" : app, head, sub, icon, "", chat));
            return;
        }
        Eyes.Jump();
        Eyes.Express(EyeMood.Surprised, 700);
        Eyes.Attend(4, 0, 700);
        // let the eyes react first, then open the card
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(480) };
        t.Tick += (_, _) => { t.Stop(); ShowNotif(new NotifData(string.IsNullOrWhiteSpace(app) ? "Notification" : app, head, sub, icon, "\uEA8F", chat)); };
        t.Start();
    }

    // =======================================================================
    //  Chats: WhatsApp / Instagram … conversations from notifications, with reply
    // =======================================================================
    private void UpdateChatsState()
    {
        var any = _chats.Threads.Count > 0;
        ChatsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ChatsListHost.Visibility = any && _openThread == null ? Visibility.Visible : Visibility.Collapsed;
        ChatView.Visibility = _openThread != null ? Visibility.Visible : Visibility.Collapsed;
        ChatsBadge.Visibility = _chats.Threads.Any(t => t.Unread > 0) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenThread(ChatThread t)
    {
        _openThread = t;
        ChatTitle.Text = t.Title;
        ChatApp.Text = t.App;
        ChatMessages.ItemsSource = t.Messages;
        t.Unread = 0;
        ReplyStatus.Visibility = Visibility.Collapsed;
        UpdateChatsState();
        ChatView.Opacity = 0;
        Motion.Show(ChatView, 240, 0, blur: 6, fromY: 8);
        ScrollChatToEnd();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => StartTyping(ReplyBox));
    }

    private void ScrollChatToEnd() => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ChatScroll.ScrollToEnd());

    private void ChatRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChatThread t) OpenThread(t);
    }

    private void ChatBack_Click(object sender, RoutedEventArgs e)
    {
        _openThread = null;
        UpdateChatsState();
        Stagger(ChatsList);
    }

    private void ReplyBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ReplyHint.Visibility = ReplyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void ReplyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SendReplyAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; StopTyping(); }
    }

    private async void ReplySend_Click(object sender, RoutedEventArgs e) => await SendReplyAsync();

    private async Task SendReplyAsync()
    {
        var t = _openThread;
        var text = ReplyBox.Text.Trim();
        if (t == null || text.Length == 0) return;
        ReplyBox.IsEnabled = false;
        ReplySend.IsEnabled = false;
        ReplyStatus.Text = "Sending…";
        ReplyStatus.Visibility = Visibility.Visible;
        var r = await _chats.ReplyAsync(t, text);
        ReplyBox.Text = "";
        ReplyBox.IsEnabled = true;
        ReplySend.IsEnabled = true;
        ReplyStatus.Text = r switch
        {
            ReplyResult.SentInline or ReplyResult.SentFromCenter => $"✓  Sent to {t.Title}",
            ReplyResult.CopiedAndOpened => $"Copied — {t.App} is opening: press Ctrl+V, then Enter",
            _ => "Couldn't send this one",
        };
        if (r is ReplyResult.SentInline or ReplyResult.SentFromCenter) { Eyes.Express(EyeMood.Happy, 1200); }
        Motion.SlideIn(ReplyStatus, 6, 260);
        ScrollChatToEnd();
        UpdateChatsState();
    }

    // ---- typing: the notch normally never takes focus; while a text box is used it may
    private void TypingBox_MouseDown(object sender, MouseButtonEventArgs e) => StartTyping((TextBox)sender);

    private void TypingBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is TextBox) return;
        StopTyping();
        if (Expanded && !Notch.IsMouseOver) RestartCollapseTimer();
    }

    private void StartTyping(TextBox box)
    {
        if (!Expanded) return;
        if (App.SnapshotDir != null) { box.Focus(); return; }
        if (!_typing)
        {
            _typing = true;
            var ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex & ~Native.WS_EX_NOACTIVATE));
            Activate();
            Native.SetForegroundWindow(_hwnd);
        }
        box.Focus();
        Keyboard.Focus(box);
        box.CaretIndex = box.Text.Length;
    }

    private void StopTyping()
    {
        if (!_typing) return;
        _typing = false;
        Keyboard.ClearFocus();
        Native.AddExStyle(_hwnd, Native.WS_EX_NOACTIVATE);
    }

    // =======================================================================
    //  Music search: YouTube suggestions as you type, open on YouTube / YouTube Music
    // =======================================================================
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        if (SearchBox.Text.Trim().Length == 0) { ShowSearchEmpty("Type a song — results appear as you type and play inside the notch", false); return; }
        _searchTimer.Start();
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchBox.Text.Trim().Length > 0)
        {
            e.Handled = true;
            _searchTimer.Stop();
            if (_results.Count == 0 || _resultsFor != SearchBox.Text.Trim()) await SearchAsync(SearchBox.Text);
            if (_results.Count > 0) await PlayResultAsync(0);
        }
        else if (e.Key == Key.Escape) { e.Handled = true; StopTyping(); }
    }

    /// <summary>YouTube results (thumbnail, title, channel, length) for the search box.</summary>
    private async Task SearchAsync(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        SearchSpinner.Visibility = Visibility.Visible;
        SearchSpinner.IsPlaying = true;
        try
        {
            var list = await YouTubeSearch.SearchAsync(query);
            if (SearchBox.Text.Trim() != query && App.SnapshotDir == null) return;   // user kept typing
            _resultsFor = query;
            _results.Clear();
            foreach (var r in list)
            {
                r.Playing = _player.Current?.Id == r.Id;
                _results.Add(r);
            }
            if (_results.Count == 0) ShowSearchEmpty($"No songs found for “{query}”", true);
            else
            {
                SearchEmpty.Visibility = Visibility.Collapsed;
                ResultsScroll.ScrollToTop();
                Stagger(ResultsList);
            }
        }
        catch (Exception ex)
        {
            App.Log("youtube search: " + ex.Message);
            ShowSearchEmpty("Couldn't reach YouTube right now", true);
        }
        finally
        {
            SearchSpinner.IsPlaying = false;
            SearchSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowSearchEmpty(string text, bool offerBrowser)
    {
        _results.Clear();
        SearchEmptyText.Text = text;
        SearchOpenYt.Visibility = offerBrowser ? Visibility.Visible : Visibility.Collapsed;
        SearchEmpty.Visibility = Visibility.Visible;
    }

    private async void Result_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is YtResult r) await PlayResultAsync(_results.IndexOf(r));
    }

    /// <summary>Plays a result inside the notch (the rest of the results become "up next") and shows it in Media.</summary>
    private async Task PlayResultAsync(int index)
    {
        if (index < 0) return;
        StopTyping();
        await _player.PlayAsync(_results, index);
        Eyes.Express(EyeMood.Happy, 1200);
        Eyes.Jump(1.1);
        TabMedia.IsChecked = true;
    }

    private void SearchOpenYt_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Trim().Length > 0) OpenYouTube(SearchBox.Text.Trim(), music: false);
    }

    // =======================================================================
    //  Activity: Claude Code sessions, Claude, ChatGPT, VS Code
    // =======================================================================
    private void OnActivityChanged(ActivityItem item, ActivityState prev)
    {
        UpdateLive();
        if (prev == ActivityState.Working && item.State == ActivityState.Done && App.SnapshotDir == null)
        {
            Eyes.Express(EyeMood.Happy, 1400);
            ShowNotif(new NotifData($"{item.App} finished", item.Title is { Length: > 0 } t2 && t2 != "Open" ? t2 : "All done", item.Detail, item.Icon, "\uE945", null, item.Brand));
            SayLater("Done! ✅", 6500, 2000);
        }
        if (_mode == Mode.Collapsed) ApplyCollapsed(animate: true);
    }

    // =======================================================================
    //  The eyes talk: speech bubble with greetings, reactions and gentle nudges
    // =======================================================================
    private readonly DispatcherTimer _bubbleTimer = new();
    // the eyes' voice (ElevenLabs / Windows) and the little bob while they speak
    private readonly VoiceService _voice = new();
    // live spoken conversation with Gemini, and the caption it shows in the pill
    private readonly LiveTalkService _talk = new();
    private readonly TalkMemory _memory = new();
    private readonly UserProfile _profile = UserProfile.Load();
    private WakeWord? _wake;

    /// <summary>Starts (or stops) listening for "Hey &lt;name&gt;" — only when there's a Gemini key to talk with.</summary>
    private void SyncWakeWord()
    {
        if (App.SnapshotDir != null) return;
        var want = _settings.WakeWord && _talk.HasKey;
        if (!want) { _wake?.Stop(); _wake = null; return; }
        if (_wake != null) return;
        _wake = new WakeWord { Strict = () => _media.Current?.IsPlaying == true };
        _wake.Heard += _ => Dispatcher.BeginInvoke(() =>
        {
            if (_talk.Active || Native.IsSessionLocked()) return;
            Eyes.Jump(1.2);
            Eyes.Express(EyeMood.Happy, 1200);
            ToggleLiveTalk();
        });
        if (!_wake.Start(_profile.AssistantName)) App.Log("wake word: " + _wake.Status);
    }
    private bool _ducked;
    private string? _liveLine;
    private readonly DispatcherTimer _talkBob = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _talkBobDir = 1;
    private DateTime _voiceQuietUntil;
    private DateTime _lastHello = DateTime.MinValue, _lastSay = DateTime.MinValue, _nextChatter = DateTime.Now.AddMinutes(9);
    private static readonly Random Rng = new();
    private static string Pick(params string[] s) => s[Rng.Next(s.Length)];

    private static string Greeting()
    {
        var h = DateTime.Now.Hour;
        return h switch
        {
            < 5 => Pick("Up late? 🌙", "Night owl mode 🦉"),
            < 12 => Pick("Good morning ☀️", "Morning! Ready to build? ☀️", "Rise and shine ☀️"),
            < 17 => Pick("Good afternoon 🌤", "Hey! Good afternoon 👋"),
            < 22 => Pick("Good evening 🌆", "Evening! 👋"),
            _ => Pick("Late one tonight? 🌙", "Good night soon? 🌙"),
        };
    }

    /// <summary>Shows a little speech bubble under the pill; the eyes glance down at it.</summary>
    private void Say(string text, int ms = 2600, bool speak = true)
    {
        if (!_settings.EyesTalk || _mode != Mode.Collapsed || _intro || _bannerOpen || _standby != null || _eyesHidden) return;
        // out loud too, unless music is playing (it would talk over your song)
        if (speak && _settings.VoiceEnabled && _media.Current?.IsPlaying != true && !_talk.Active && App.SnapshotDir == null) _ = _voice.SpeakAsync(text);
        BubbleText.Text = text;
        Bubble.Margin = new Thickness(0, Notch.ActualHeight + 3, 0, 0);
        Bubble.Visibility = Visibility.Visible;
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        Motion.Pop(BubbleScale, 0.45, 1, 520, 0.55);
        BubbleY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(420)) { EasingFunction = Motion.Spring(0.5) });
        Eyes.Attend(0, 2.2, Math.Min(ms, 1400));
        _lastSay = DateTime.Now;
        _bubbleTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _bubbleTimer.Stop();
        _bubbleTimer.Start();
    }

    /// <summary>Says something once the island is back to its small self (e.g. after a pop-up card).</summary>
    private void SayLater(string text, int delayMs, int ms = 2600)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        t.Tick += (_, _) => { t.Stop(); Say(text, ms); };
        t.Start();
    }

    private void HideBubble()
    {
        _bubbleTimer.Stop();
        if (Bubble.Visibility != Visibility.Visible) return;
        Motion.Scale(BubbleScale, 0.85, 200);
        Motion.To(Bubble, OpacityProperty, 0, 170, done: () => { if (Bubble.Opacity < 0.05) Bubble.Visibility = Visibility.Collapsed; });
    }

    private void OnEyesReaction(string what)
    {
        switch (what)
        {
            case "hover":
                if (DateTime.Now - _lastHello < TimeSpan.FromSeconds(25)) return;
                _lastHello = DateTime.Now;
                Say(Rng.Next(4) == 0 ? Greeting() : Pick("Hi! 👋", "Hello there! 👋", "Hey you 😊", "Oh, hi!", "Yes? 👀", "Hiya! ✨", "Hello, friend 🙂"));
                break;
            case "poke":
                Say(Pick("Ouch!", "Boop! 👉", "Hey! 😳", "That tickles 😆", "Hehe", "Careful! 👀", "Again? 🙃"), 1600);
                break;
            case "angry":
                Say(Pick("Stop it! 😤", "Hey, not cool 😠", "Okay, OKAY! 😤"), 2200);
                break;
            case "wink":
                Say(Pick("Hehe 😉", "Love you too ❤️", "😉✨", "You're the best 💖"), 1900);
                break;
        }
    }

    /// <summary>Every few minutes, when it's calm, a friendly nudge.</summary>
    private void MaybeChatter()
    {
        if (!_settings.EyesTalk || _mode != Mode.Collapsed || DateTime.Now < _nextChatter || DateTime.Now - _lastSay < TimeSpan.FromMinutes(3)) return;
        _nextChatter = DateTime.Now.AddMinutes(Rng.Next(9, 17));
        if (_live != null && DateTime.Now - _live.StartedAt > TimeSpan.FromMinutes(40)) { Say($"{_live.App} has been busy a while ⏳", 3200); return; }
        if (_media.Current?.IsPlaying == true) { Say(Pick("Great vibes 🎶", "This one slaps 🎧", "Dancing over here 💃"), 3000); return; }
        Say(Pick("Stay hydrated 💧", "You got this 💪", "Time to stretch? 🧘", "Deep breath… 😌", "Nice focus 🔥", "Blink a little 👀", "Proud of you ✨"), 3200);
    }

    // =======================================================================
    //  Standby clock (macOS lock-screen style) — after a while away, or on demand
    // =======================================================================
    private StandbyWindow? _standby;

    /// <param name="manual">opened on purpose (button / Ctrl+Alt+P): a privacy screen that stays until you click or press a key</param>
    private void ShowStandby(bool manual = false)
    {
        if (App.SnapshotDir != null) return;
        if (_standby != null) { if (manual) _standby.Dismiss("toggle"); return; }   // the shortcut toggles it
        App.Log($"standby: showing ({(manual ? "manual" : "idle")})");
        if (Expanded) Collapse();
        HideBubble();
        StopTyping();
        _standby = new StandbyWindow(_media, manual, () => _settings.LyricsEnabled ? _currentLyrics : null);
        _standby.Closed += (_, _) =>
        {
            _standby = null;
            SayLater(Pick("Welcome back! 👋", "Hey, you're back 😊", "Missed you 👀"), 600, 2400);
        };
        _standby.Show();
    }

    private void MaybeStandby()
    {
        var mins = _settings.StandbyAfterMinutes;
        if (mins <= 0 || _standby != null || Native.IsSessionLocked()) return;
        if (Frost.IdleTime() < TimeSpan.FromMinutes(mins)) return;
        if (IsFullscreenForeground()) return;   // don't cover a video you're watching
        ShowStandby();
    }

    private bool IsFullscreenForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || !Native.GetWindowRect(fg, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return false;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfo(Native.MonitorFromWindow(fg, 2), ref mi) &&
               r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    private void StandbyNow_Click(object sender, RoutedEventArgs e)
    {
        // let the notch fold away first so the blurred picture is of your desktop, not of the open notch
        if (Expanded) Collapse();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        t.Tick += (_, _) => { t.Stop(); ShowStandby(manual: true); };
        t.Start();
    }

    // ---- Ctrl+Alt+P: privacy screen from anywhere
    private const int HotkeyPrivacy = 0x4E01;

    private void RegisterPrivacyHotkey()
    {
        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyPrivacy) { handled = true; ShowStandby(manual: true); }   // WM_HOTKEY
            if (msg == 0x0312 && w.ToInt32() == HotkeyTalk) { handled = true; ToggleLiveTalk(); }
            return IntPtr.Zero;
        });
        // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 'P'
        if (!Native.RegisterHotKey(_hwnd, HotkeyPrivacy, 0x0002 | 0x0001 | 0x4000, 0x50))
            App.Log("privacy hotkey: Ctrl+Alt+P is taken by another app");
        if (!Native.RegisterHotKey(_hwnd, HotkeyTalk, 0x0002 | 0x0001 | 0x4000, 0x54))   // Ctrl+Alt+T
            App.Log("talk hotkey: Ctrl+Alt+T is taken by another app");
    }
    private const int HotkeyTalk = 0x4E54;

    private void LivePillSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.LiveInPill = LivePillSwitch.IsChecked == true;
        _settings.Save();
        _activityLine = null;
        UpdateLive();
        if (_mode == Mode.Collapsed) ApplyCollapsed(animate: true);
    }

    private void StandbySwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.StandbyAfterMinutes = StandbySwitch.IsChecked == true ? 5 : 0;
        _settings.Save();
    }

    private void TalkSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.EyesTalk = TalkSwitch.IsChecked == true;
        _settings.Save();
        if (!_settings.EyesTalk) HideBubble();
    }

    // =======================================================================
    //  Notifications: a macOS-style frosted banner that drops out of the notch
    // =======================================================================
    public sealed record NotifData(string App, string Title, string Body, ImageSource? Icon, string Glyph, ChatThread? Chat, Color? Tint = null, TodoItem? Todo = null,
        Action? Act = null, string? ActLabel = null);

    /// <summary>A notification as shown in the Alerts tab and the "View all" list.</summary>
    public sealed class NotifEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public NotifData Data { get; init; } = null!;
        public DateTime Time { get; init; } = DateTime.Now;
        public string App => Data.App;
        public string AppUpper => Data.App.ToUpperInvariant();
        public string Title => Data.Title;
        public string Body => Data.Body;
        public ImageSource? Icon => Data.Icon;
        public string Glyph => Data.Glyph;
        public Visibility GlyphVisibility => Data.Icon == null ? Visibility.Visible : Visibility.Collapsed;
        public string When
        {
            get
            {
                var d = DateTime.Now - Time;
                return d.TotalSeconds < 60 ? "now" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes}m ago" : d.TotalHours < 24 ? $"{(int)d.TotalHours}h ago" : Time.ToString("ddd HH:mm");
            }
        }
        public void Touch() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(When)));
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<NotifEntry> _history = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<NotifEntry> _listItems = new();
    private readonly List<NotifData> _notifQueue = new();
    private NotifData? _notif;
    private double _notifLeft;
    private bool _bannerOpen, _bannerHover;
    private readonly DispatcherTimer _notifTick = new() { Interval = TimeSpan.FromMilliseconds(50) };

    private void AddHistory(NotifData d)
    {
        _history.Insert(0, new NotifEntry { Data = d });
        while (_history.Count > 60) _history.RemoveAt(_history.Count - 1);
        if (!(Expanded && _tabIndex == 7)) NotifsBadge.Visibility = Visibility.Visible;
        UpdateNotifsState();
    }

    private void UpdateNotifsState()
    {
        NotifsEmpty.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifsCount.Text = _history.Count == 0 ? "Notifications" : $"{_history.Count} notification{(_history.Count == 1 ? "" : "s")}";
    }

    private void ShowNotif(NotifData d)
    {
        AddHistory(d);
        if (Expanded || _countdown > 0 || _intro) return;
        if (_bannerOpen && _notif != null)
        {
            _notifQueue.Add(d);
            UpdateBannerStack();
            return;
        }
        PresentBanner(d, fresh: true);
    }

    private void PresentBanner(NotifData d, bool fresh)
    {
        _notif = d;
        HideBubble();
        BannerApp.Text = d.App.ToUpperInvariant();
        BannerTitle.Text = d.Title.Length > 0 ? d.Title : d.App;
        BannerBody.Text = d.Body;
        BannerBody.Visibility = d.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BannerIcon.Source = d.Icon;
        BannerGlyph.Text = d.Glyph;
        BannerGlyph.Visibility = d.Icon == null ? Visibility.Visible : Visibility.Collapsed;
        var tint = d.Tint ?? (d.Icon is System.Windows.Media.Imaging.BitmapSource b ? ColorUtil.Dominant(b) : _theme.Current);
        BannerTintStop.Color = AccentTheme.WithAlpha(tint, 0x48);
        if (d.Act != null)
        {
            // one clear action (e.g. "Update now"), always visible
            BannerReply.Visibility = Visibility.Collapsed;
            BannerOpenText.Text = d.ActLabel ?? "Open";
            BannerActions.BeginAnimation(HeightProperty, null);
            BannerActions.BeginAnimation(OpacityProperty, null);
            BannerActions.Height = 34;
            BannerActions.Opacity = 1;
        }
        else if (d.Todo != null)
        {
            // reminders: ✓ Done and Snooze, always visible
            BannerReply.Visibility = Visibility.Visible;
            BannerReplyGlyph.Text = "";
            BannerReplyText.Text = "Done";
            BannerOpenText.Text = "Snooze 10 min";
            BannerActions.BeginAnimation(HeightProperty, null);
            BannerActions.BeginAnimation(OpacityProperty, null);
            BannerActions.Height = 34;
            BannerActions.Opacity = 1;
        }
        else
        {
            BannerReply.Visibility = d.Chat != null ? Visibility.Visible : Visibility.Collapsed;
            BannerReplyGlyph.Text = "";
            BannerReplyText.Text = "Reply";
            BannerOpenText.Text = d.App.Length is > 0 and <= 14 ? $"Open {d.App}" : "Open";
            if (!_bannerHover) { BannerActions.BeginAnimation(HeightProperty, null); BannerActions.Height = 0; BannerActions.BeginAnimation(OpacityProperty, null); BannerActions.Opacity = 0; }
        }
        UpdateBannerStack();

        // sit just under the floating pill
        var top = PillFloat + Notch.ActualHeight + 10;
        Banner.Margin = new Thickness(0, top, 0, 0);
        Banner.Visibility = Visibility.Visible;
        Banner.UpdateLayout();
        var w = BannerCard.ActualWidth;
        var h = BannerCard.ActualHeight;
        BannerGlass.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 20, 20);

        // frosted glass: blur what's on screen behind the card
        const double pad = 40;
        var x = (Root.ActualWidth - w) / 2;
        var shot = Frost.Behind(this, new Rect(x, top, w, h), pad);
        BannerBackdrop.Source = shot;
        BannerBackdrop.Width = w + 2 * pad;
        BannerBackdrop.Height = h + 2 * pad;
        BannerBackdrop.Margin = new Thickness(-pad);   // bigger than the card, but must not change its size

        _notifLeft = d.Todo != null ? 20000 : d.Act != null ? 15000 : d.Chat != null ? 7500 : 5800;
        _notifTick.Stop();
        _notifTick.Start();

        if (!fresh || _bannerOpen)
        {
            // next one in the stack: a quick swap
            Motion.SlideIn(BannerText, 10, 340);
            Motion.Pop(BannerIconScale, 0.6, 1, 500, 0.6);
            _bannerOpen = true;
            return;
        }
        _bannerOpen = true;

        // 1) a droplet squeezes out of the notch and falls…
        Eyes.Attend(0, 3, 900);
        BannerCard.Opacity = 0;
        BannerDrop.Opacity = 1;
        BannerDropY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 8, TimeSpan.FromMilliseconds(300)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        BannerDropScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.35, 0.85, TimeSpan.FromMilliseconds(300)));
        BannerDropScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.35, 1.3, TimeSpan.FromMilliseconds(300)));

        // 2) …splats, and blooms into the frosted card
        var bloom = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(290) };
        bloom.Tick += (_, _) =>
        {
            bloom.Stop();
            var splatX = new DoubleAnimation(1.6, TimeSpan.FromMilliseconds(140));
            var splatY = new DoubleAnimation(0.6, TimeSpan.FromMilliseconds(140));
            BannerDropScale.BeginAnimation(ScaleTransform.ScaleXProperty, splatX);
            BannerDropScale.BeginAnimation(ScaleTransform.ScaleYProperty, splatY);
            BannerDrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) { BeginTime = TimeSpan.FromMilliseconds(80) });

            BannerCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            BannerScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.08, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut } });
            BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.12, 1, TimeSpan.FromMilliseconds(480)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
            BannerY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(480)) { EasingFunction = Motion.Spring(0.4) });
            Motion.Pop(BannerIconScale, 0.3, 1, 640, 0.75);
            Motion.SlideIn(BannerText, 8, 420, 140);
        };
        bloom.Start();
    }

    private void UpdateBannerStack()
    {
        var more = _notifQueue.Count;
        BannerMore.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        BannerMoreText.Text = $"View all · {more + 1}";
        if (more > 0 && BannerStack.Visibility != Visibility.Visible)
        {
            BannerStack.Visibility = Visibility.Visible;
            BannerStack.Opacity = 0;
            Motion.Show(BannerStack, 260, 0, blur: 0, fromY: -6);
        }
        else if (more == 0) BannerStack.Visibility = Visibility.Collapsed;
    }

    private void OnNotifTick()
    {
        if (!_bannerOpen) { _notifTick.Stop(); return; }
        if (!_bannerHover) _notifLeft -= _notifTick.Interval.TotalMilliseconds;   // pauses while you hover
        if (_notifLeft <= 0) NextNotif();
    }

    private void NextNotif()
    {
        if (_notifQueue.Count > 0)
        {
            var next = _notifQueue[0];
            _notifQueue.RemoveAt(0);
            PresentBanner(next, fresh: false);
            return;
        }
        CloseBanner();
    }

    /// <summary>The card shrinks back into a droplet that rises into the notch.</summary>
    private void CloseBanner(bool instant = false)
    {
        _notifTick.Stop();
        _notif = null;
        _notifQueue.Clear();
        if (!_bannerOpen) return;
        _bannerOpen = false;
        _bannerHover = false;
        if (instant) { Banner.Visibility = Visibility.Collapsed; return; }
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseIn };
        BannerScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        BannerScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.15, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        BannerCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { BeginTime = TimeSpan.FromMilliseconds(80) });
        BannerStack.Visibility = Visibility.Collapsed;
        var rise = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        rise.Tick += (_, _) =>
        {
            rise.Stop();
            BannerDropScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 0.3, TimeSpan.FromMilliseconds(260)));
            BannerDropScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.1, 0.4, TimeSpan.FromMilliseconds(260)));
            BannerDropY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, -16, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            BannerDrop.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames = { new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.1)), new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)) },
                Duration = TimeSpan.FromMilliseconds(260),
            });
            var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            done.Tick += (_, _) => { done.Stop(); if (!_bannerOpen) Banner.Visibility = Visibility.Collapsed; Eyes.Blink(); };
            done.Start();
        };
        rise.Start();
    }

    private void Banner_MouseEnter(object sender, MouseEventArgs e)
    {
        _bannerHover = true;
        Motion.To(BannerClose, OpacityProperty, 1, 160);
        Motion.To(BannerActions, OpacityProperty, 1, 200);
        Motion.To(BannerActions, HeightProperty, 34, 260, Motion.Spring(0.3));
        Motion.Scale(BannerScale, 1.015, 260);
    }

    private void Banner_MouseLeave(object sender, MouseEventArgs e)
    {
        _bannerHover = false;
        Motion.To(BannerClose, OpacityProperty, 0, 200);
        Motion.To(BannerActions, OpacityProperty, 0, 160);
        Motion.To(BannerActions, HeightProperty, 0, 220, Motion.Out);
        Motion.Scale(BannerScale, 1, 260);
    }

    private void Banner_Click(object sender, MouseButtonEventArgs e)
    {
        var d = _notif;
        if (d == null || e.Handled) return;
        OpenNotification(d);
    }

    /// <summary>What clicking a notification does: open the chat / the to-do list / the app.</summary>
    private void OpenNotification(NotifData d)
    {
        CloseBannerList();
        if (d.Chat != null) { OpenChatFromBanner(d.Chat); return; }
        if (d.Act is { } act) { CloseBanner(); act(); return; }
        if (d.Todo != null) { CloseBanner(instant: true); Expand(); Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => TabTodo.IsChecked = true); return; }
        if (WindowFor(d.App) is { } h && h != IntPtr.Zero) AppsService.Focus(h);
        CloseBanner();
    }

    private void NotifRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotifEntry n) OpenNotification(n.Data);
    }

    private void NotifsClear_Click(object sender, RoutedEventArgs e)
    {
        var i = 0;
        foreach (var c in Enumerable.Range(0, _history.Count).Select(k => NotifsList.ItemContainerGenerator.ContainerFromIndex(k)).OfType<FrameworkElement>())
        {
            var t = new TranslateTransform();
            c.RenderTransform = t;
            Motion.To(t, TranslateTransform.XProperty, 420, 320, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseIn }, i * 40);
            Motion.To(c, OpacityProperty, 0, 300, null, i * 40);
            i++;
        }
        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320 + i * 40) };
        done.Tick += (_, _) => { done.Stop(); _history.Clear(); UpdateNotifsState(); };
        done.Start();
    }

    // =======================================================================
    //  "View all": the stack fans out into a scrolling list (iPhone style)
    // =======================================================================
    private readonly DispatcherTimer _listClose = new() { Interval = TimeSpan.FromMilliseconds(1400) };
    private bool _listOpen;

    private void BannerViewAll_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var all = new List<NotifData>();
        if (_notif != null) all.Add(_notif);
        all.AddRange(_notifQueue);
        ShowBannerList(all);
    }

    private void ShowBannerList(List<NotifData> items)
    {
        if (items.Count == 0) return;
        CloseBanner(instant: true);
        _listItems.Clear();
        foreach (var d in items) _listItems.Add(_history.FirstOrDefault(h => h.Data == d) ?? new NotifEntry { Data = d });
        BannerListTitle.Text = $"{items.Count} notifications";

        var top = PillFloat + Notch.ActualHeight + 10;
        BannerList.Margin = new Thickness(0, top, 0, 0);
        BannerList.Visibility = Visibility.Visible;
        BannerList.Opacity = 0;
        BannerList.UpdateLayout();
        var w = BannerList.ActualWidth;
        var h = BannerList.ActualHeight;
        BannerListGlass.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 22, 22);
        const double pad = 40;
        BannerListBackdrop.Source = Frost.Behind(this, new Rect((Root.ActualWidth - w) / 2, top, w, h), pad);
        BannerListBackdrop.Width = w + 2 * pad;
        BannerListBackdrop.Height = h + 2 * pad;
        BannerListBackdrop.Margin = new Thickness(-pad);

        // the stack unfolds: every card starts tucked under the first one and slides down into its place
        BannerList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        for (int i = 0; i < _listItems.Count; i++)
        {
            if (BannerListItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var y = new TranslateTransform(0, -i * (c.ActualHeight + 6) * 0.92);
            var s = new ScaleTransform(1 - Math.Min(i, 3) * 0.04, 1);
            c.RenderTransformOrigin = new Point(0.5, 0);
            c.RenderTransform = new TransformGroup { Children = { s, y } };
            c.Opacity = i < 3 ? 1 : 0;
            Motion.To(y, TranslateTransform.YProperty, 0, 520, Motion.Spring(0.3), 60 + i * 55);
            Motion.To(s, ScaleTransform.ScaleXProperty, 1, 480, Motion.Out, 60 + i * 55);
            Motion.To(c, OpacityProperty, 1, 300, null, 60 + i * 55);
        }
        _listOpen = true;
        _listClose.Stop();
        _listClose.Interval = TimeSpan.FromSeconds(12);   // closes by itself if you never look at it
        _listClose.Start();
    }

    private void CloseBannerList()
    {
        _listClose.Stop();
        if (!_listOpen) return;
        _listOpen = false;
        // fold back up into a stack and fade
        var n = _listItems.Count;
        for (int i = 0; i < n; i++)
        {
            if (BannerListItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
            var y = new TranslateTransform();
            c.RenderTransform = y;
            Motion.To(y, TranslateTransform.YProperty, -i * (c.ActualHeight + 6) * 0.92, 300, new QuadraticEase { EasingMode = EasingMode.EaseIn }, (n - i) * 25);
        }
        Motion.To(BannerList, OpacityProperty, 0, 260, null, 140, done: () => { if (!_listOpen) BannerList.Visibility = Visibility.Collapsed; });
    }

    private void BannerList_MouseEnter(object sender, MouseEventArgs e) => _listClose.Stop();

    private void BannerList_MouseLeave(object sender, MouseEventArgs e)
    {
        _listClose.Stop();
        _listClose.Interval = TimeSpan.FromMilliseconds(1400);
        _listClose.Start();
    }

    private void BannerListClose_Click(object sender, RoutedEventArgs e) => CloseBannerList();

    private void BannerListClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var it in _listItems) _history.Remove(it);
        UpdateNotifsState();
        var i = 0;
        foreach (var c in Enumerable.Range(0, _listItems.Count).Select(k => BannerListItems.ItemContainerGenerator.ContainerFromIndex(k)).OfType<FrameworkElement>())
        {
            var t = new TranslateTransform();
            c.RenderTransform = t;
            Motion.To(t, TranslateTransform.XProperty, 420, 300, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseIn }, i * 45);
            i++;
        }
        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260 + i * 45) };
        done.Tick += (_, _) => { done.Stop(); CloseBannerList(); };
        done.Start();
    }

    // =======================================================================
    //  To-do list with reminders
    // =======================================================================
    private readonly TodoService _todo = new();
    private DateTime? _chipDue;

    private void UpdateTodoState()
    {
        TodoEmpty.Visibility = _todo.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var dueSoon = _todo.Items.Count(t => !t.Done && t.Due is { } d && d.Date <= DateTime.Today);
        TodoBadge.Visibility = dueSoon > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TodoBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TodoHint.Visibility = TodoBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTodoPreview();
    }

    private void UpdateTodoPreview()
    {
        var (_, parsed) = TodoService.Parse(TodoBox.Text);
        var due = parsed ?? _chipDue;
        TodoDuePreview.Visibility = due != null ? Visibility.Visible : Visibility.Collapsed;
        if (due != null) TodoDueText.Text = "⏰ " + new TodoItem { Due = due }.DueText;
    }

    private void TodoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; AddTodo(); }
        else if (e.Key == Key.Escape) { e.Handled = true; StopTyping(); }
    }

    private void TodoAdd_Click(object sender, RoutedEventArgs e) => AddTodo();

    private void AddTodo()
    {
        var (text, parsed) = TodoService.Parse(TodoBox.Text);
        if (text.Length == 0) return;
        var item = _todo.Add(text, parsed ?? _chipDue);
        TodoBox.Text = "";
        _chipDue = null;
        UpdateTodoPreview();
        UpdateTodoState();
        Eyes.Express(EyeMood.Happy, 1200);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (TodoList.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement c)
            {
                var s = new ScaleTransform(0.9, 0.9);
                c.RenderTransformOrigin = new Point(0.5, 0.5);
                c.RenderTransform = s;
                Motion.Pop(s, 0.85, 1, 480, 0.5);
                c.Opacity = 0;
                Motion.To(c, OpacityProperty, 1, 260);
            }
        });
    }

    private void TodoChip_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _chipDue = ((FrameworkElement)sender).Tag as string switch
        {
            "15m" => now.AddMinutes(15),
            "1h" => now.AddHours(1),
            "tonight" => DateTime.Today.AddHours(20) > now ? DateTime.Today.AddHours(20) : now.AddMinutes(30),
            "tomorrow" => DateTime.Today.AddDays(1).AddHours(9),
            _ => null,
        };
        UpdateTodoPreview();
        Motion.Pop(new ScaleTransform(), 0.9, 1);
        StartTyping(TodoBox);
    }

    private void TodoCheck_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TodoItem t) return;
        if (((FrameworkElement)sender).RenderTransform is ScaleTransform s) Motion.Pop(s, 0.6, 1, 420, 0.7);
        var wasDone = t.Done;
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wasDone ? 0 : 380) };
        t.Done = !t.Done;   // tick shows at once, the list re-sorts a moment later
        delay.Tick += (_, _) => { delay.Stop(); _todo.Sort(); _todo.Save(); UpdateTodoState(); };
        delay.Start();
        if (!wasDone) Eyes.Express(EyeMood.Happy, 1200);
    }

    private void TodoDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TodoItem t) { _todo.Remove(t); UpdateTodoState(); }
    }

    /// <summary>Checks for reminders that are due and drops a banner for each.</summary>
    private void CheckReminders()
    {
        foreach (var t in _todo.DueNow())
        {
            App.Log("reminder: " + t.Text);
            Eyes.Jump(1.25);
            Eyes.Express(EyeMood.Surprised, 900);
            ShowNotif(new NotifData("Reminder", t.Text, $"Due {t.Due:HH:mm}  ·  from your to-do list", null, "", null, Color.FromRgb(0xFF, 0x9F, 0x0A), t));
            if (!TalkAboutReminder(t)) SayLater("Reminder! ⏰", 7000, 2000);
        }
        UpdateTodoState();
    }

    /// <summary>When a to-do is due, the notch speaks up: it starts a live chat (or joins the one going on), tells you, and offers ideas.</summary>
    private bool TalkAboutReminder(TodoItem t)
    {
        if (!_settings.TalkOnReminder || !_talk.HasKey || Native.IsSessionLocked() || App.SnapshotDir != null) return false;
        var note = $"(Reminder from the user's to-do list: \"{t.Text}\" is due now, at {t.Due:HH:mm}. Tell them about it in a friendly way and give two or three short, practical ideas or first steps for doing it. Then ask if they'd like help with it.)";
        if (_talk.Active) { _ = _talk.SayToModelAsync(note); return true; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };   // after the reminder card has popped
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (_talk.Active) { await _talk.SayToModelAsync(note); return; }
            _voice.Stop();
            App.Log("reminder: starting a live chat about it");
            await _talk.StartAsync(LivePrompt(), note);
        };
        timer.Start();
        return true;
    }

    private void OpenChatFromBanner(ChatThread chat)
    {
        CloseBanner(instant: true);
        _pendingChat = chat;
        Expand();
    }

    private void BannerReply_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_notif?.Todo is { } todo) { if (!todo.Done) _todo.Toggle(todo); UpdateTodoState(); Eyes.Express(EyeMood.Happy, 1200); NextNotif(); return; }
        if (_notif?.Chat is { } c) OpenChatFromBanner(c);
    }

    private void BannerOpen_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var d = _notif;
        if (d == null) return;
        if (d.Act is { } act) { NextNotif(); act(); return; }
        if (d.Todo is { } todo) { _todo.Snooze(todo, TimeSpan.FromMinutes(10)); NextNotif(); return; }
        if (d.Chat != null) ChatService.OpenApp(d.App);
        else if (WindowFor(d.App) is { } h && h != IntPtr.Zero) AppsService.Focus(h);
        NextNotif();
    }

    private void BannerClose_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NextNotif();
    }

    /// <summary>Main window of a running app, matched by process name or description.</summary>
    private static IntPtr WindowFor(string app)
    {
        var key = new string(app.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                var pn = p.ProcessName.ToLowerInvariant();
                var desc = new string((p.MainModule?.FileVersionInfo.FileDescription ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                if (pn == key || desc == key || (key.Length >= 4 && (pn.Contains(key) || desc.Contains(key)))) return p.MainWindowHandle;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    /// <summary>Picks the job shown live in the pill and refreshes the Activity header / badge.</summary>
    private void UpdateLive()
    {
        var working = _activity.Items.Where(i => i.State == ActivityState.Working).ToList();
        var done = _activity.Items.Count(i => i.State == ActivityState.Done);
        ActivityBadge.Visibility = working.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityEmpty.Visibility = _activity.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivitySummary.Text = working.Count == 0 && done == 0 ? "All quiet"
            : string.Join("  ·  ", new[] { working.Count > 0 ? $"{working.Count} working" : null, done > 0 ? $"{done} done" : null }.Where(s => s != null));

        // most interesting first: Claude Code, then terminal jobs, then the apps
        var live = working.OrderBy(i => i.Kind == "session" ? 0 : i.Kind == "terminal" ? 1 : 2).ThenBy(i => i.StartedAt).FirstOrDefault();
        var brand = live?.Brand ?? Color.FromRgb(0x5E, 0xE0, 0xA8);
        ActivityPulse.Fill = new SolidColorBrush(brand);
        if (live != _live)
        {
            _live = live;
            if (live != null)
            {
                LiveIcon.Source = live.Icon;
                LiveInitial.Text = live.Icon == null ? live.Initial : "";
                LiveInitial.Foreground = live.BrandBrush;
                LiveApp.Text = live.App;
                LiveRing.Stroke = live.BrandRing;
                LiveRingGlow.Color = brand;
                foreach (var d in new[] { LiveDot1, LiveDot2, LiveDot3 }) d.Fill = live.BrandBrush;
                LiveTime.Foreground = live.BrandBrush;
            }
        }
        if (live != null) LiveTime.Text = live.Elapsed;
        var line = live == null || !_settings.LiveInPill ? null : $"{live.App}  ·  {(live.Detail.Length > 0 ? live.Detail : "working…")}";
        if (line != _activityLine)
        {
            _activityLine = line;
            if (_mode == Mode.Collapsed) ApplyCollapsed(animate: true);
        }
    }

    /// <summary>Swaps the pill between its normal layout and the live-activity layout.</summary>
    private void SetLiveMode(bool on)
    {
        if (on != _liveShown)
        {
            _liveShown = on;
            var (show, hide) = on ? (new UIElement[] { LiveLeft, LiveRight }, new UIElement[] { MiniLeft, MiniRight })
                                  : (new UIElement[] { MiniLeft, MiniRight }, new UIElement[] { LiveLeft, LiveRight });
            foreach (var h in hide) Motion.Hide(h, 140);
            foreach (var s in show) { s.Visibility = Visibility.Visible; s.Opacity = 0; Motion.Show(s, 320, 120, blur: 6, fromY: 4); }
            if (on)
            {
                Eyes.Express(EyeMood.Surprised, 500);
                Eyes.Attend(-3, 0, 1200);
                if (Rng.Next(2) == 0) SayLater(Pick("On it! 💪", "Ooh, working 🧠", "Let's go 🚀", "Busy busy 🐝"), 700, 2000);
            }
            return;
        }
        // keep the two layouts from overlapping after the volume / Wi-Fi overlay
        if (on && !_hudShown) { MiniLeft.Visibility = Visibility.Collapsed; MiniRight.Visibility = Visibility.Collapsed; }
    }

    private void StartLiveLoops()
    {
        LiveRingSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.3)) { RepeatBehavior = RepeatBehavior.Forever });
        var i = 0;
        foreach (var d in new[] { LiveDot1, LiveDot2, LiveDot3 })
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.1), RepeatBehavior = RepeatBehavior.Forever };
            var t0 = i * 0.15;
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t0))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t0 + 0.25))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0.25, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t0 + 0.55))));
            d.BeginAnimation(OpacityProperty, a);
            i++;
        }
        // breathing dot in the Activity header
        var grow = new DoubleAnimation(1, 2.4, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Out };
        ActivityPulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ActivityPulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ActivityPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ActivityItem a && a.Hwnd != IntPtr.Zero) AppsService.Focus(a.Hwnd);
    }

    private void OpenYouTube(string query, bool music)
    {
        var url = music
            ? "https://music.youtube.com/search?q=" + Uri.EscapeDataString(query)
            : "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("open youtube: " + ex.Message); }
        StopTyping();
        Eyes.Express(EyeMood.Happy, 1200);
        Collapse();
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
        if (_mode != Mode.Collapsed || _intro) return;
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
            foreach (var el in new UIElement[] { MiniLeft, MiniRight, LiveLeft, LiveRight })
                if (el.Visibility == Visibility.Visible) Motion.Hide(el, 120);
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
        foreach (var el in _liveShown ? new UIElement[] { LiveLeft, LiveRight } : new UIElement[] { MiniLeft, MiniRight })
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
        if (_tick % 8 == 4) _activity.Refresh();
        if (_tick % 4 == 0) { _activity.Tick(); UpdateLive(); MaybeChatter(); }
        if (_tick % 20 == 10) MaybeStandby();
        if (_tick % 8 == 2) RefreshPillFrost();
        if (_tick % 4 == 1 && !_intro && App.SnapshotDir == null) CheckReminders();
        if (_tick % 30 == 7 && !_intro && App.SnapshotDir == null) MaybeAutoInstall();
        if (_tick % 4 == 3 && !_intro && App.SnapshotDir == null) BreakTick();
        if (_tick % 120 == 61 && !_intro && App.SnapshotDir == null) MaybeStartProactiveChat();
    }

    // =======================================================================
    //  Healthy breaks: after a while of using the PC, a short break you can't skip
    // =======================================================================
    private double _workSeconds;
    private bool _breakWarned, _breakPostponed;
    private DateTime? _breakUntil;
    private DateTime _lastBreakTick = DateTime.Now;
    private int _breakEscapes;

    /// <summary>Counts the time you actually use the PC; time away counts as rest (and a long enough rest resets it).</summary>
    private void BreakTick()
    {
        var now = DateTime.Now;
        var dt = Math.Clamp((now - _lastBreakTick).TotalSeconds, 0, 5);
        _lastBreakTick = now;
        // a break you asked for at a set time: starts then (once you're at the PC), whatever the schedule switch says
        if (_breakUntil == null && _settings.ScheduledBreakAt is { } at && now >= at && !Native.IsSessionLocked())
        {
            var len = Math.Clamp(_settings.ScheduledBreakMinutes, 1, 120);
            _settings.ScheduledBreakAt = null;
            _settings.Save();
            StartBreak("scheduled", len);
            return;
        }
        if (!_settings.BreaksEnabled || _breakUntil != null) { UpdateBreakUi(); return; }

        var idle = Frost.IdleTime();
        if (Native.IsSessionLocked() || idle.TotalMinutes >= Math.Max(1, _settings.BreakMinutes))
        {
            // you were away at least as long as a break: that counts as one
            if (_workSeconds > 60) App.Log($"breaks: you rested ({idle.TotalMinutes:0} min away), timer reset");
            _workSeconds = 0; _breakWarned = false; _breakPostponed = false;
            UpdateBreakUi();
            return;
        }
        if (idle.TotalSeconds < 120) _workSeconds += dt;   // reading without touching anything still counts for a bit

        var total = Math.Max(5, _settings.BreakEveryMinutes) * 60.0;
        if (!_breakWarned && _workSeconds >= total - 60)
        {
            _breakWarned = true;
            var mins = _settings.BreakMinutes;
            ShowNotif(new NotifData("Notch", "Break in 1 minute ☕", $"Save your work — a {mins}-minute break is coming", null, "", null,
                Color.FromRgb(0x5E, 0x8B, 0xFF),
                Act: _breakPostponed ? null : () => { _breakPostponed = true; _breakWarned = false; _workSeconds = total - 5 * 60; Say("Okay, 5 more minutes ⏳", 2400); },
                ActLabel: _breakPostponed ? null : "5 more minutes"));
            SayLater("Break in a minute! ☕", 600, 2600);
        }
        if (_workSeconds >= total) StartBreak("time");
        if (_tick % 40 == 3) UpdateBreakUi();
    }

    private TimeSpan UntilNextBreak => TimeSpan.FromSeconds(Math.Max(0, Math.Max(5, _settings.BreakEveryMinutes) * 60.0 - _workSeconds));

    private void UpdateBreakUi()
    {
        BreakRowText.Text = _breakUntil == null && _settings.ScheduledBreakAt is { } sb && sb > DateTime.Now ? $"Breaks · planned {sb:HH:mm}"
            : !_settings.BreaksEnabled ? "Breaks"
            : _breakUntil != null ? "Breaks · on break now"
            : $"Breaks · next in {Fmt(UntilNextBreak)}";
        BreakSwitch.ToolTip = $"A {_settings.BreakMinutes}-minute break after every {Fmt(TimeSpan.FromMinutes(_settings.BreakEveryMinutes))} of using the PC. " +
                              "The screen stays covered until it's over; the notch keeps you company. Emergency only: hold Ctrl+Shift+E for 5 seconds.";
        static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} m" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
    }

    private void BreakSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.BreaksEnabled = BreakSwitch.IsChecked == true;
        _settings.Save();
        _workSeconds = 0; _breakWarned = false; _breakPostponed = false;
        UpdateBreakUi();
        if (_settings.BreaksEnabled) Say($"I'll give you a {_settings.BreakMinutes}-min break every {_settings.BreakEveryMinutes / 60.0:0.#} h ☕", 3200);
    }

    /// <summary>Covers the screen for the break, starts a chat (or speaks up) and keeps you company until it's over.</summary>
    private void StartBreak(string why, int? minutes = null)
    {
        if (_breakUntil != null || App.SnapshotDir != null) return;
        var mins = Math.Clamp(minutes ?? _settings.BreakMinutes, 1, 120);
        _breakUntil = DateTime.Now.AddMinutes(mins);
        _breakEscapes = 0;
        App.Log($"breaks: starting a {mins}-min break ({why}, worked {_workSeconds / 60:0} min)");
        if (_standby != null && !_standby.IsBreak) { _standby.Dismiss("break starts"); _standby = null; }
        if (Expanded) Collapse();
        HideBubble();
        StopTyping();

        var w = new StandbyWindow(_media, manual: true, breakUntil: _breakUntil);
        _standby = w;
        w.EscapeAttempt += left => OnBreakEscape(left);
        w.EmergencyExit += () =>
        {
            App.Log("breaks: ended early with the emergency keys");
            _ = _talk.SayToModelAsync("(The user ended the break early with the emergency keys. Say you hope everything is okay, in one sentence.)");
            _workSeconds = Math.Max(0, Math.Max(5, _settings.BreakEveryMinutes) * 60.0 - 30 * 60);   // another break in half an hour
        };
        w.Closed += (_, _) =>
        {
            if (_standby == w) _standby = null;
            var finished = _breakUntil != null && DateTime.Now >= _breakUntil.Value.AddSeconds(-2);
            _breakUntil = null;
            _talk.KeepAlive = false;
            if (finished) _workSeconds = 0;
            _breakWarned = false; _breakPostponed = false;
            UpdateBreakUi();
            if (finished)
            {
                Eyes.Express(EyeMood.Happy, 1600);
                if (_talk.Active) _ = _talk.SayToModelAsync("(The break is over and the screen is open again. Welcome them back in one cheerful sentence.)");
                else SayLater(Pick("Break's over — welcome back! 💪", "Refreshed? Let's go! ✨", "Welcome back! 😊"), 500, 3000);
            }
        };
        w.Show();
        UpdateBreakUi();

        var reason = why == "time" ? $"the user has been using the computer for about {_settings.BreakEveryMinutes / 60.0:0.#} hours, so a {mins}-minute break has just started"
                   : why == "scheduled" ? $"the {mins}-minute break the user planned earlier has just started"
                   : $"the user asked for a {mins}-minute break, and it has just started";
        var note = $"(Break time: {reason}. " +
                   "Their screen is covered until it's over and they can't end it early. Tell them warmly, suggest a couple of things to do (stretch, water, rest the eyes, a short walk), " +
                   "and keep them company: chat, ask how their day is going. If they ask to end the break early, kindly say no and encourage them — you have no way to end it.)";
        if (_talk.HasKey)
        {
            _talk.KeepAlive = true;   // stays on for the whole break
            if (_talk.Active) _ = _talk.SayToModelAsync(note);
            else { _voice.Stop(); _ = _talk.StartAsync(LivePrompt(), note); }
        }
        else _ = _voice.SpeakAsync($"Time for a {mins} minute break! Stand up, stretch, and rest your eyes. I'll open the screen again when it's over.");
    }

    /// <summary>Someone tried to close the break early: the notch says something about it (more firmly each time).</summary>
    private void OnBreakEscape(TimeSpan left)
    {
        _breakEscapes++;
        var remaining = $"{(int)left.TotalMinutes} min {left.Seconds:00} s";
        App.Log($"breaks: escape attempt #{_breakEscapes} ({remaining} left)");
        if (_talk.Active)
            _ = _talk.SayToModelAsync($"(The user just tried to close the break screen early (attempt {_breakEscapes}); {remaining} of the break are left. " +
                                      (_breakEscapes == 1 ? "Kindly tell them it isn't over yet and why the break matters." : "Be a little firmer but still friendly and playful; suggest something to do instead.") + ")");
        else
            _ = _voice.SpeakAsync(_breakEscapes == 1
                ? $"Not yet! There's still {(int)Math.Ceiling(left.TotalMinutes)} minutes of break. Stretch a little."
                : Pick("Nice try! The break isn't over.", "Hands off the keyboard, you've earned this rest.", "Still on break. Drink some water!"));
    }

    /// <summary>Loops that run forever but only cost anything while their element is visible.</summary>
    private void StartAmbientLoops()
    {
        StartLiveLoops();
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
        var tr = NotchTopRadius;
        Notch.CornerRadius = new CornerRadius(tr, tr, r, r);
        double w = Notch.ActualWidth, h = Notch.ActualHeight;
        if (w <= 0 || h <= 0) return;
        r = Math.Min(r, Math.Min(w, h) / 2);
        tr = Math.Min(tr, Math.Min(w, h) / 2);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(tr, 0), true, true);
            c.LineTo(new Point(w - tr, 0), false, false);
            if (tr > 0.1) c.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(w, h - r), false, false);
            c.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r, h), false, false);
            c.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(0, tr), false, false);
            if (tr > 0.1) c.ArcTo(new Point(tr, 0), new Size(tr, tr), 0, false, SweepDirection.Clockwise, false, false);
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
        if (_hwnd == IntPtr.Zero || App.SnapshotDir != null || !IsLoaded || _backing == null) return;
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
        if (Expanded || _intro) return;
        var wasPeek = _mode == Mode.Peek;
        if (_bannerOpen) CloseBanner(instant: true);
        HideBubble();

        // frosted glass: blur what's behind the spot the open island will cover
        if (App.SnapshotDir == null)
        {
            const double pad = 50;
            var area = new Rect((Root.ActualWidth - ExpandedW) / 2, FloatGap, ExpandedW, ExpandedHChat + 12);
            var shot = Frost.Behind(this, area, pad);
            FrostImage.Source = Frost.LooksBlank(shot) ? null : shot;   // Windows sometimes hands back an all-black picture
            FrostImage.Width = ExpandedW + 2 * pad;
            FrostImage.Height = area.Height + 2 * pad;
            FrostImage.Margin = new Thickness(0, -pad, 0, 0);
            FrostImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)));
        }

        _mode = Mode.Expanded;
        _peekTimer.Stop();
        _collapseTimer.Stop();
        SetGlass(Mode.Expanded);
        SetBlur(true);
        Motion.To(NotchHost, OpacityProperty, 1, 150);

        // float free of the top edge: drop down, round the top corners, melt the ears away
        Motion.To(_hostY, TranslateTransform.YProperty, FloatGap, 560, Motion.Spring(0.35));
        Motion.To(this, NotchTopRadiusProperty, ExpandedRadius - 4, 480, Motion.Out);
        Motion.To(EarL, OpacityProperty, 0, 220);
        Motion.To(EarR, OpacityProperty, 0, 220);

        if (TabApps.IsChecked == true) RefreshApps();
        UpdateMediaPanel(trackChanged: false);

        Morph(ExpandedW, ExpandedHeight, ExpandedRadius, 560, 0.32);
        Motion.Hide(CollapsedView, 110);
        if (wasPeek && _card != null) { Motion.Hide(_card, 110); _card = null; }
        // hovering a message pop-up opens straight into that conversation, ready to reply
        if (_pendingChat is { } chat)
        {
            _pendingChat = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { TabChats.IsChecked = true; OpenThread(chat); });
        }
        ExpandedView.Opacity = 0;
        Motion.Show(ExpandedView, 320, 150, blur: 14, fromY: -12);

        Motion.To(Ambient, OpacityProperty, 1, 600, Motion.Out, 120);
        Motion.To(PillGlass, OpacityProperty, 0, 300);
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

    // =======================================================================
    //  The small pill floats just below the top edge, on the same frosted, music-tinted glass as the open notch
    // =======================================================================
    private const double PillFloat = 6;

    private void EnterFloatingPill(int ms)
    {
        Motion.To(_hostY, TranslateTransform.YProperty, PillFloat, ms, Motion.Spring(0.25));
        Motion.To(this, NotchTopRadiusProperty, CollapsedRadius, ms, Motion.Out);
        Motion.To(EarL, OpacityProperty, 0, 200);
        Motion.To(EarR, OpacityProperty, 0, 200);
        Motion.To(Ambient, OpacityProperty, 1, ms);
        Motion.To(PillGlass, OpacityProperty, 1, ms);
        Motion.To(Rim, OpacityProperty, 0.8, ms);
        Motion.To(NotchShadow, OpacityProperty, 0.55, ms);
        RefreshPillFrost();
    }

    /// <summary>Re-takes the blurred picture behind the small pill (cheap: a small strip at the top of the screen).</summary>
    private void RefreshPillFrost()
    {
        if (_mode != Mode.Collapsed || _intro || App.SnapshotDir != null || NotchHost.Opacity < 0.5) return;
        const double pad = 30;
        var w = CollapsedWTicker + 30;
        var area = new Rect((Root.ActualWidth - w) / 2, PillFloat, w, CollapsedH + TickerH + 6);
        var shot = Frost.Behind(this, area, pad);
        if (shot == null || Frost.LooksBlank(shot)) { FrostImage.Source = null; return; }   // the pill's own glass still shows
        FrostImage.Source = shot;
        FrostImage.Width = w + 2 * pad;
        FrostImage.Height = area.Height + 2 * pad;
        FrostImage.Margin = new Thickness(0, -pad, 0, 0);
        FrostImage.BeginAnimation(OpacityProperty, null);
        FrostImage.Opacity = 1;
    }

    private void Collapse()
    {
        if (!Expanded || _countdown > 0 || _draggingOut) return;
        _mode = Mode.Collapsed;
        StopTyping();
        SetGlass(Mode.Collapsed);
        Motion.Hide(ExpandedView, 130, blur: 12);
        SetAmbientDrift(false);
        Motion.To(NotchGlow, OpacityProperty, 0, 300);
        EnterFloatingPill(420);
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
        if (Expanded || _countdown > 0 || _intro) return false;
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
        _pendingChat = null;
        SetGlass(Mode.Collapsed);
        if (_card != null) Motion.Hide(_card, 130, blur: 10);
        _card = null;
        Motion.To(NotchGlow, OpacityProperty, 0, 250);
        EnterFloatingPill(360);
        CollapsedView.Opacity = 0;
        Motion.Show(CollapsedView, 260, 150, blur: 8);
        _targetW = _targetH = -1;
        ApplyCollapsed(animate: true);
    }

    /// <summary>The media tab grows taller when the lyrics stage is showing.</summary>
    private double ExpandedHeight =>
        _tabIndex is >= 3 and <= 8 ? ExpandedHChat
        : _tabIndex == 0 && LineBox.Visibility == Visibility.Visible && _media.Current != null ? ExpandedHLyrics : ExpandedH;

    private bool _eyesHidden;

    private void SetEyesHidden(bool hide)
    {
        if (hide == _eyesHidden) return;
        _eyesHidden = hide;
        if (hide)
        {
            HideBubble();
            Motion.To(Eyes, OpacityProperty, 0, 220, done: () => { if (_eyesHidden) Eyes.Visibility = Visibility.Collapsed; });
        }
        else
        {
            Eyes.Visibility = Visibility.Visible;
            Motion.To(Eyes, OpacityProperty, 1, 360, Motion.Out, 120);
        }
    }

    private void ApplyCollapsed(bool animate)
    {
        if (_mode != Mode.Collapsed || _intro) return;
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
        // while music plays the pill is about the song: the eyes step aside, lyrics run underneath
        SetEyesHidden(playing && !_hudShown);

        // live activity takes over the pill while something works (music still wins)
        var live = _settings.LiveInPill && _live != null && !playing && !_hudShown;
        SetLiveMode(live);

        var w = _hudShown ? Math.Max(HudW, showTicker ? CollapsedWTicker : 0)
              : showTicker ? Math.Max(CollapsedWTicker, live ? CollapsedWLive : 0)
              : live ? CollapsedWLive : MediaActive ? CollapsedWMedia : CollapsedW;
        var h = CollapsedH + (showTicker ? TickerH : 0);
        Morph(w, h, showTicker ? 22 : CollapsedRadius, animate ? 480 : 0, 0.3);
    }

    /// <summary>Fades the island out over fullscreen video; still opens on hover.</summary>
    /// <summary>A game, video or presentation fills the screen in front (the notch shouldn't interrupt it).</summary>
    private bool FullscreenAppInFront()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || !Native.GetWindowRect(fg, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return false;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        return Native.GetMonitorInfo(Native.MonitorFromWindow(fg, 2), ref mi) &&
               r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // =======================================================================
    //  The notch starts a chat by itself now and then: hobby news or a bit of history
    // =======================================================================
    private readonly DateTime _startedAt = DateTime.Now;

    private void MaybeStartProactiveChat()
    {
        if (!_settings.ProactiveChats || !_talk.HasKey || _talk.Active || _breakUntil != null || _standby != null) return;
        var now = DateTime.Now;
        if (now.Hour < 9 || now.Hour >= 21 || now - _startedAt < TimeSpan.FromMinutes(10)) return;   // not at night, not right after start
        if (_profile.ProactiveDay.Date != now.Date) { _profile.ProactiveDay = now.Date; _profile.ProactiveToday = 0; }
        if (_profile.ProactiveToday >= 3 || now - _profile.LastProactive < TimeSpan.FromHours(3)) return;
        // only when you're there, not typing away, nothing playing, nothing full screen, not locked
        var idle = Frost.IdleTime();
        if (idle < TimeSpan.FromSeconds(20) || idle > TimeSpan.FromSeconds(90)) return;
        if (Native.IsSessionLocked() || _media.Current?.IsPlaying == true || FullscreenAppInFront() || _mode != Mode.Collapsed) return;

        _profile.LastProactive = now;
        _profile.ProactiveToday++;
        _profile.Save();
        var name = string.IsNullOrWhiteSpace(_profile.UserName) ? "the user" : _profile.UserName;
        var topic = _profile.Interests.Count > 0 && Rng.Next(3) != 0
            ? $"something new and interesting about one of their interests ({string.Join(", ", _profile.Interests)}) — if you can search Google, find a real update from the last few days; otherwise share a fun fact or tip about it"
            : $"a short, fascinating 'on this day in history' fact for {now:d MMMM}";
        var opening = $"(You are starting this chat yourself — {name} didn't ask. Greet them by name in a few words, then share {topic}. " +
                      "Two or three sentences, then ask a light question to keep the conversation going. If they say they're busy, wish them well and keep it short.)";
        App.Log("proactive chat: starting");
        _voice.Stop();
        _ = _talk.StartAsync(LivePrompt(), opening);
    }

    private void UpdateFullscreenFade()
    {
        if (!_settings.HideInFullscreen || _mode != Mode.Collapsed || App.SnapshotDir != null || _intro) return;
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
        if (has && _lastCharging.HasValue && _lastCharging != charging)
        {
            ShowCharge(charging, pct);
            SayLater(charging ? Pick("Yum, power ⚡", "Charging up! 🔋", "Mmm, electricity ⚡") : Pick("Unplugged! I'll be careful 🔋", "Running free 🏃"), 4300, 2400);
        }
        if (has && !charging && pct <= 15 && _lastLowWarn != true) { _lastLowWarn = true; SayLater("Battery's low 🥺 plug me in?", 400, 3200); }
        if (charging || pct > 20) _lastLowWarn = false;
        _lastCharging = has ? charging : null;
    }

    // =======================================================================
    //  Media
    // =======================================================================
    private bool MediaActive =>
        _media.Current is { } m && (m.IsPlaying || (DateTime.Now - _lastPlaying).TotalSeconds < 45);

    private void OnMediaChanged()
    {
        UpdateDucking();
        var m = _media.Current;
        var trackChanged = m?.TrackKey != _lastTrackKey;
        if (trackChanged && m?.IsPlaying == true && _lastTrackKey != null && _mode == Mode.Collapsed && Rng.Next(3) == 0)
            SayLater(Pick("Ooh, nice track 🎶", "Bop bop 🎵", "I love this one 🎧", "Turn it up! 🔊"), 3800, 2200);
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
        var wanted = _settings.LyricsEnabled && (m.IsSpotify || m.AppId == YouTubePlayer.AppId || LyricsToggle.IsChecked == true);
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
            return h.gesture switch
            {
                "Clap" => ClapCount(h.progress) switch
                {
                    1 => _countdown > 0 ? "👏  Clap again to cancel" : "👏  Clap again to lock",
                    2 => _countdown > 0 ? "👏👏  Cancelling…" : "👏👏  Locking…  (once more = restart)",
                    3 => _countdown > 0 ? "👏👏👏" : "👏👏👏  Restart…  (once more = voice)",
                    _ => _talk.HasKey ? (_talk.Active ? "👏👏👏👏  Bye!" : "👏👏👏👏  Let's talk")
                         : _settings.VoiceEnabled ? "👏👏👏👏  Voice off" : "👏👏👏👏  Voice on",
                },
                "Closed_Fist" => _countdown > 0 ? "✊  Hold to cancel" : "✊  Hold to lock",
                _ => "🖐  Hold to restart",
            };
        if (_talk.Active) return _liveLine ?? (_talk.State == LiveTalkService.TalkState.Connecting ? "🎙  Connecting…" : "🎙  Listening…");
        if (_settings.CaptionsEnabled && !string.IsNullOrWhiteSpace(_captionLine)) return _captionLine;
        if (LyricLine() is { Length: > 0 } l) { _tickerIsLyric = true; return l; }
        if (_activityLine != null) return _activityLine;
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
        else if (_settings.LyricsEnabled && m != null && (m.IsSpotify || m.AppId == YouTubePlayer.AppId || LyricsToggle.IsChecked == true) && _lyricsKey != null)
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
    private FrameworkElement[] Panels => new FrameworkElement[] { MediaPanel, AppsPanel, ShelfPanel, PowerPanel, ChatsPanel, SearchPanel, ActivityPanel, NotifsPanel, TodoPanel };
    private RadioButton[] Tabs => new[] { TabMedia, TabApps, TabShelf, TabPower, TabChats, TabSearch, TabActivity, TabNotifs, TabTodo };

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
        if (idx == 4) UpdateChatsState();
        if (idx == 7) { NotifsBadge.Visibility = Visibility.Collapsed; foreach (var n in _history) n.Touch(); UpdateNotifsState(); }
        if (idx == 8) { foreach (var ti in _todo.Items) ti.Touch(); UpdateTodoState(); }
        if (idx == 6) { _activity.Refresh(); Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Stagger(ActivityList)); }
        if (idx == 5) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => StartTyping(SearchBox));
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
    private void Restart_Click(object sender, RoutedEventArgs e) => StartCountdown("Restarting", "Restart now", CancelHint(), PowerService.Restart);
    private void Shutdown_Click(object sender, RoutedEventArgs e) => StartCountdown("Shutting down", "Shut down now", CancelHint(), PowerService.Shutdown);

    /// <summary>How to stop a countdown, naming only the controls that are switched on.</summary>
    private string CancelHint(string? prefix = null)
    {
        var ways = new List<string>();
        if (_settings.GesturesEnabled) ways.Add("close your hand ✊");
        if (_settings.ClapsEnabled) ways.Add("clap twice 👏👏");
        ways.Add("press Cancel");
        var s = ways.Count == 1 ? ways[0] : string.Join(", ", ways.Take(ways.Count - 1)) + " or " + ways[^1];
        s = char.ToUpper(s[0]) + s[1..] + " to stop";
        return prefix == null ? s : prefix + " — " + char.ToLower(s[0]) + s[1..];
    }

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
    //  Updates: new GitHub releases download by themselves and install while you're away
    // =======================================================================
    private readonly UpdateService _updates = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(90) };
    private string? _announcedUpdate;

    private void InitUpdates()
    {
        UpdateSwitch.IsChecked = _settings.AutoUpdate;
        _updates.Changed += () => Dispatcher.BeginInvoke(OnUpdateChanged);
        UpdateUpdateUi();
        if (App.SnapshotDir != null) return;
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);   // first look 90 s after start, then every 6 hours
            await _updates.CheckAsync(download: _settings.AutoUpdate);
        };
        _updateTimer.Start();

        var args = Environment.GetCommandLineArgs();
        // QA: check now and install as soon as it's downloaded
        if (args.Contains("--update-now"))
        {
            _updates.Changed += () => Dispatcher.BeginInvoke(() => { if (_updates.State == UpdateState.Ready) InstallUpdate(); });
            _ = _updates.CheckAsync(download: true);
        }

        // just updated → say so
        var i = Array.IndexOf(args, "--updated");
        if (i >= 0)
        {
            var tag = i + 1 < args.Length ? args[i + 1] : "v" + _updates.CurrentText;
            App.Log($"update: now running {tag}");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                ShowNotif(new NotifData("Windows Notch", $"Updated to {tag} ✨", "You're on the newest version", null, "", null, AccentTheme.Default,
                    Act: () => OpenUrl(UpdateService.ReleasesPage), ActLabel: "What's new"));
                Eyes.Express(EyeMood.Happy, 1600);
            };
            t.Start();
        }
    }

    private void OnUpdateChanged()
    {
        UpdateUpdateUi();
        var tag = _updates.NewTag;
        if (tag == null || tag == _announcedUpdate) return;
        if (_updates.State == UpdateState.Ready)
        {
            _announcedUpdate = tag;
            ShowNotif(new NotifData("Windows Notch", $"Update ready · {tag}",
                _settings.AutoUpdate ? "It installs by itself next time you step away — or now (takes a few seconds)" : "Install it now — takes a few seconds",
                null, "", null, AccentTheme.Default, Act: InstallUpdate, ActLabel: "Update now"));
        }
        else if (_updates.State == UpdateState.Available && _settings.LastAnnouncedUpdate != tag)
        {
            // this copy can't replace itself (developer build or a protected folder): point to the download
            _announcedUpdate = tag;
            _settings.LastAnnouncedUpdate = tag;
            _settings.Save();
            ShowNotif(new NotifData("Windows Notch", $"{tag} is out", "Download the new version from GitHub", null, "", null, AccentTheme.Default,
                Act: () => OpenUrl(UpdateService.ReleasesPage), ActLabel: "Download"));
        }
    }

    private void UpdateUpdateUi()
    {
        UpdateChipText.Text = _updates.State switch
        {
            UpdateState.Checking => "Checking…",
            UpdateState.Downloading => $"Downloading {(int)(_updates.Progress * 100)}%",
            UpdateState.Ready => $"Update to {_updates.NewTag}",
            UpdateState.Available => $"{_updates.NewTag} — download",
            UpdateState.UpToDate => $"v{_updates.CurrentText} · up to date",
            UpdateState.Failed => _updates.Error ?? "Update failed",
            _ => $"v{_updates.CurrentText} · check updates",
        };
        UpdateChip.ToolTip = _updates.State switch
        {
            UpdateState.Ready => "Restart the notch on the new version now",
            UpdateState.Available => "Open the download page",
            _ => "Check GitHub for a new version",
        };
    }

    private async void UpdateChip_Click(object sender, RoutedEventArgs e)
    {
        switch (_updates.State)
        {
            case UpdateState.Ready: InstallUpdate(); break;
            case UpdateState.Available: OpenUrl(UpdateService.ReleasesPage); break;
            default: await _updates.CheckAsync(download: true); break;
        }
    }

    private void UpdateSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoUpdate = UpdateSwitch.IsChecked == true;
        _settings.Save();
    }

    /// <summary>While you're away (3 min idle, nothing playing, notch closed) a downloaded update installs by itself.</summary>
    private void MaybeAutoInstall()
    {
        if (!_settings.AutoUpdate || _updates.State != UpdateState.Ready) return;
        if (_mode != Mode.Collapsed || _countdown > 0 || _typing || _media.Current?.IsPlaying == true) return;
        if (Frost.IdleTime() < TimeSpan.FromMinutes(3)) return;
        App.Log("update: installing while idle");
        InstallUpdate();
    }

    private void InstallUpdate()
    {
        if (!_updates.Install()) { OpenUrl(UpdateService.ReleasesPage); return; }
        _standby?.Dismiss("update");
        Quit_Click(this, new RoutedEventArgs());   // the island retracts and the new version comes back up
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
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
        var cam = _gestureState switch
        {
            "watching" => "Watching  ·  ✊ lock  ·  🖐 restart",
            "loading" or "ready" => "Loading hand-tracking model…",
            "off" or "" => _settings.GesturesEnabled ? "Paused" : "Off  ·  ✊ lock  ·  🖐 restart",
            _ when _gestureState.StartsWith("error") => _gestureState.Replace("error: ", "⚠ "),
            _ => _gestureState,
        };
        var clapLine = _clapState.StartsWith("error") ? _clapState.Replace("error: ", "⚠ ")
            : _clapState == "listening" ? "Listening  ·  👏👏 lock  ·  👏👏👏 restart" : "Claps paused";
        // show what's actually on: camera, claps, or both (camera line + a short clap note)
        GestureStatus.Text = !_settings.ClapsEnabled ? cam
            : !_settings.GesturesEnabled ? clapLine
            : _clapState.StartsWith("error") ? clapLine
            : _gestureState == "watching" ? "✊ / 👏👏 lock  ·  🖐 / 👏👏👏 restart"
            : cam;
        GestureStatus.ToolTip = _settings.ClapsEnabled && _settings.GesturesEnabled ? cam + "\n" + clapLine : null;
    }

    // =======================================================================
    //  Clap control (microphone) — for PCs whose camera can't see hands well
    // =======================================================================
    private static double ClapProgress(int n) => n / 3.0;
    private static int ClapCount(double progress) => (int)Math.Round(progress * 3);

    private void StartClaps()
    {
        if (App.SnapshotDir != null) return;
        if (_claps == null)
        {
            _claps = new ClapListener();
            _claps.StatusChanged += st => Dispatcher.BeginInvoke(() =>
            {
                App.Log("claps: " + st);
                _clapState = st;
                UpdateGestureUi();
            });
            _claps.Analyzer.Clap += n => Dispatcher.BeginInvoke(() => OnClapHeard(n));
            _claps.Analyzer.Sequence += (n, ms) => Dispatcher.BeginInvoke(() => OnClapSequence(n, ms));
        }
        _claps.Start();
    }

    /// <summary>Times claps must be ignored: hands on the keyboard/mouse (typing sounds), sound playing, locked, just acted.</summary>
    private string? ClapBlocker(double spanMs = 0)
    {
        if (DateTime.UtcNow < _clapCooldown) return "cooldown";
        if (Native.IsSessionLocked()) return "locked";
        // the notch's own voice comes out of the speakers: syllables can sound like claps
        if (_talk.Active) return "live talk on";
        if (_voice.Speaking || DateTime.UtcNow < _voiceQuietUntil) return "notch speaking";
        if (_media.Current?.IsPlaying == true) return "media playing";
        // nobody claps with a hand on the mouse: any input during the claps means it was typing or clicking
        if (Frost.IdleTime().TotalMilliseconds < spanMs + 500) return "keyboard/mouse in use";
        return null;
    }

    private void OnClapHeard(int n)
    {
        if (!_settings.ClapsEnabled) return;
        if (ClapBlocker() is { } why) { App.Log($"clap {n} heard, ignored ({why})"); return; }
        App.Log($"clap {n} heard");
        if (n == 1 && _countdown == 0 && _mode != Mode.Collapsed) return;   // a lone clap only shows on the pill
        SetHold(("Clap", ClapProgress(Math.Min(n, 4))), clap: true);
    }

    private void OnClapSequence(int n, int spanMs)
    {
        if (_hold is { gesture: "Clap" }) SetHold(null, clap: true);
        if (!_settings.ClapsEnabled || n < 2) return;
        if (ClapBlocker(spanMs) is { } why)
        {
            App.Log($"claps: {n} claps ignored ({why})");
            return;
        }
        if (n > 4) return;   // applause, not a command
        _clapCooldown = DateTime.UtcNow.AddSeconds(4);
        App.Log($"claps fired: {n} claps in {spanMs} ms");

        if (n == 2)
        {
            if (_countdown > 0) CancelCountdown();   // two claps during a countdown = cancel
            else { Collapse(); PowerService.Lock(); }
        }
        else if (n == 4)
        {
            if (_talk.HasKey) ToggleLiveTalk();
            else SetVoice(!_settings.VoiceEnabled, announce: true);
        }
        else if (_countdown == 0)
        {
            StartCountdown("Restarting", "Restart now", CancelHint("Three claps 👏👏👏 heard"), PowerService.Restart);
        }
    }

    // =======================================================================
    //  Voice — the eyes speak (ElevenLabs voice with a key, otherwise the Windows voice)
    // =======================================================================
    private void InitVoice()
    {
        _voice.ApiKey = VoiceService.Unprotect(_settings.VoiceKey);
        _voice.VoiceId = string.IsNullOrWhiteSpace(_settings.VoiceId) ? VoiceService.DefaultVoiceId : _settings.VoiceId;
        _voice.Volume = _settings.VoiceVolume;
        VoiceSwitch.IsChecked = _settings.VoiceEnabled;
        _talkBob.Tick += (_, _) => { _talkBobDir = -_talkBobDir; Eyes.Nod(_talkBobDir); };
        _voice.SpeakingChanged += on =>
        {
            UpdateVoiceUi();
            if (on)
            {
                _talkBob.Start();
                _bubbleTimer.Stop();           // keep the bubble up while the words are spoken
            }
            else
            {
                _talkBob.Stop();
                _voiceQuietUntil = DateTime.UtcNow.AddSeconds(1.5);   // echoes fade before claps count again
                _bubbleTimer.Interval = TimeSpan.FromMilliseconds(900);
                _bubbleTimer.Stop();
                _bubbleTimer.Start();
            }
        };
        UpdateVoiceUi();
    }

    private void UpdateVoiceUi()
    {
        VoiceChipText.Text = !_settings.VoiceEnabled ? "Voice off"
            : _voice.LastError != null && _voice.HasKey ? "⚠ " + _voice.LastError
            : _voice.HasKey ? "ElevenLabs voice" : "Windows voice";
        VoiceChip.ToolTip = (_voice.HasKey ? "Speaking with your ElevenLabs voice" : "Speaking with the Windows voice. Add an ElevenLabs key for a nicer one.")
                            + "\nClick to set up the voice · 4 claps 👏👏👏👏 turn it on/off";
    }

    /// <summary>Turns the voice on or off; <paramref name="announce"/> says so out loud (used by the 4-clap toggle).</summary>
    private void SetVoice(bool on, bool announce = false)
    {
        _settings.VoiceEnabled = on;
        if (on && !_settings.EyesTalk) { _settings.EyesTalk = true; TalkSwitch.IsChecked = true; }   // a voice needs words
        _settings.Save();
        VoiceSwitch.IsChecked = on;
        UpdateVoiceUi();
        if (on)
        {
            var line = Pick("I can talk now! 😊", "Hello! You can hear me now 👋", "Voice on! Hi there ✨");
            Say(line, 3000, speak: false);
            _ = _voice.SpeakAsync(line);   // even if music plays: you just asked for it
        }
        else
        {
            _voice.Stop();
        _talk.Stop("quit");
            if (announce) Say("Okay, I'll be quiet 🤫", 2200, speak: false);
        }
    }

    private void VoiceSwitch_Click(object sender, RoutedEventArgs e) => SetVoice(VoiceSwitch.IsChecked == true);

    // =======================================================================
    //  Live talk — a spoken conversation with the notch (Gemini Live API)
    // =======================================================================
    private void InitLive()
    {
        _talk.ApiKey = VoiceService.Unprotect(_settings.GeminiKey);
        _talk.VoiceName = string.IsNullOrWhiteSpace(_settings.GeminiVoice) ? "Kore" : _settings.GeminiVoice;
        _talk.Tools = LiveTools();
        _talk.MusicPlaying = () => _media.Current?.IsPlaying == true;
        _talk.Exchange += (you, notch) => _memory.Add(you, notch);
        _talk.ToolHandler = (name, args) => Dispatcher.InvokeAsync(() => RunVoiceActionAsync(name, args)).Task.Unwrap();
        _talk.StateChanged += (st, msg) => Dispatcher.BeginInvoke(() => OnLiveState(st, msg));
        _talk.ModelText += t => Dispatcher.BeginInvoke(() => { _liveLine = "✨  " + Tail(t); RefreshLiveLine(); });
        _talk.UserText += t => Dispatcher.BeginInvoke(() => { _liveLine = "🗣  " + Tail(t); RefreshLiveLine(); });
        UpdateTalkUi();
    }

    // ------------------------------------------------------------------ actions Gemini can take while you talk
    private static object Str(string description) => new { type = "STRING", description };

    /// <summary>What the notch can do when you ask out loud. Restart / shut down are deliberately not here.</summary>
    private static object[] LiveTools() => new object[]
    {
        new { name = "open_app", description = "Open or switch to an app on the user's Windows PC by its name (e.g. VS Code, Chrome, Spotify, WhatsApp, Word, File Explorer, Settings, Calculator, Start menu). If it's already open it comes to the front.",
              parameters = new { type = "OBJECT", properties = new { name = Str("The app's name as the user said it") }, required = new[] { "name" } } },
        new { name = "play_music", description = "Search YouTube for a song, artist or playlist and play the top result in the notch's music player.",
              parameters = new { type = "OBJECT", properties = new { query = Str("What to play, e.g. 'Calm Down Rema' or 'lofi beats'") }, required = new[] { "query" } } },
        new { name = "media_control", description = "Control whatever music or video is playing on the PC.",
              parameters = new { type = "OBJECT", properties = new { action = new { type = "STRING", description = "What to do", @enum = new[] { "play", "pause", "stop", "next", "previous" } } }, required = new[] { "action" } } },
        new { name = "now_playing", description = "Find out what song or video is playing on the PC right now." },
        new { name = "web_search", description = "Search the web in the user's browser.",
              parameters = new { type = "OBJECT", properties = new { query = Str("The search") }, required = new[] { "query" } } },
        new { name = "open_website", description = "Open a website in the user's browser.",
              parameters = new { type = "OBJECT", properties = new { url = Str("The address, e.g. youtube.com or https://github.com") }, required = new[] { "url" } } },
        new { name = "add_todo", description = "Add a task to the user's to-do list in the notch. Include any time in the text so a reminder is set (e.g. 'call mom at 5pm', 'stretch in 20 min').",
              parameters = new { type = "OBJECT", properties = new { text = Str("The task in plain words, with the time if there is one") }, required = new[] { "text" } } },
        new { name = "lock_pc", description = "Lock the PC (Windows lock screen, needs the password to get back). Only when the user clearly asks to lock it." },
        new { name = "list_open_apps", description = "List the apps and windows that are open on the PC right now." },
        new { name = "close_app", description = "Close an open app or window by name (e.g. Chrome, VS Code, Notepad, File Explorer). Apps may still ask to save unsaved work.",
              parameters = new { type = "OBJECT", properties = new { name = Str("The app or window name as the user said it") }, required = new[] { "name" } } },
        new { name = "privacy_screen", description = "Turn the notch's privacy screen on or off: a big clock over a blurred screen that hides what's on the PC (it does not lock it). Use for 'put on privacy', 'hide my screen', 'privacy off'.",
              parameters = new { type = "OBJECT", properties = new { on = new { type = "BOOLEAN", description = "true = show it, false = hide it" } }, required = new[] { "on" } } },
        new { name = "write_file", description = "Write something the user asked for (a letter, email, essay, story, CV, notes, a list, code…) and save it as a file where they want. Compose the complete text yourself, nicely formatted (use # for headings, - for bullets, **bold**). Ask where to save it only if they didn't say; default is Documents\\Notch.",
              parameters = new
              {
                  type = "OBJECT",
                  properties = new
                  {
                      content = Str("The full text to write"),
                      file_name = Str("A short file name, e.g. 'Letter to the landlord'. Add an extension only if the user asked for one (.txt, .md, .py…)"),
                      folder = Str("Where to save: Desktop, Documents, Downloads, a sub-folder like 'Documents\\Letters', or a full path such as 'D:\\School'"),
                      format = new { type = "STRING", description = "File type when the name has no extension (docx = Word, the default for letters and documents)", @enum = new[] { "docx", "txt", "md", "html", "csv", "json", "py", "js", "cs" } },
                      overwrite = new { type = "BOOLEAN", description = "Replace an existing file with the same name. Only if the user clearly asked." },
                      open_after = new { type = "BOOLEAN", description = "Open the file when it's saved (default true)" },
                  },
                  required = new[] { "content", "file_name" },
              } },
        new { name = "remember_profile", description = "Save what you learn about the user: their name, the name they give you, and their hobbies / interests (or remove interests they no longer have). Call it as soon as they tell you.",
              parameters = new
              {
                  type = "OBJECT",
                  properties = new
                  {
                      user_name = Str("The user's name, as they want to be called"),
                      assistant_name = Str("The name the user gives you"),
                      add_interests = new { type = "ARRAY", items = new { type = "STRING" }, description = "Hobbies or interests to remember, e.g. football, guitar, coding" },
                      remove_interests = new { type = "ARRAY", items = new { type = "STRING" }, description = "Interests to forget" },
                  },
              } },
        new { name = "remember_note", description = "Save something important to remember long-term so you stay on track with the user: a goal, a plan, a project, a deadline, a decision, a preference, or what you agreed to do next. One short sentence per note.",
              parameters = new { type = "OBJECT", properties = new { note = Str("The note, e.g. 'Studying for the maths exam on 15 December'") }, required = new[] { "note" } } },
        new { name = "forget_note", description = "Remove a saved note that is done or no longer true (or 'all').",
              parameters = new { type = "OBJECT", properties = new { match = Str("Words from the note to remove, or 'all'") }, required = new[] { "match" } } },
        new { name = "search_memory", description = "Search your older conversations with the user (beyond the recent ones you already know), e.g. when they ask what you said about something before.",
              parameters = new { type = "OBJECT", properties = new { query = Str("What to look for, e.g. 'exam' or 'birthday plan'") }, required = new[] { "query" } } },
        new { name = "break_status", description = "How long until the user's next computer break (or how much of the current break is left), and the break schedule." },
        new { name = "take_break_now", description = "Start a computer break right now, as long as the user wants (the screen is covered and the break can't be cancelled). Only when the user asks for a break.",
              parameters = new { type = "OBJECT", properties = new { minutes = new { type = "INTEGER", description = "How long, in minutes (1–120). Leave out for the usual break length." } } } },
        new { name = "schedule_break", description = "Plan a computer break for later, at a time the user chooses and for as long as they want. Once planned it can't be cancelled. Use at_time (24-hour HH:mm, e.g. 15:00) or in_minutes.",
              parameters = new
              {
                  type = "OBJECT",
                  properties = new
                  {
                      at_time = Str("When, as 24-hour HH:mm (e.g. 15:00). Today, or tomorrow if that time has passed."),
                      in_minutes = new { type = "INTEGER", description = "Or: how many minutes from now" },
                      minutes = new { type = "INTEGER", description = "Length of the break in minutes (1–120)" },
                  },
                  required = new[] { "minutes" },
              } },
        new { name = "set_breaks", description = "Turn healthy computer breaks on or off, or change how often and how long they are (e.g. every 90 minutes, 10-minute breaks). There is no way to end a break early.",
              parameters = new
              {
                  type = "OBJECT",
                  properties = new
                  {
                      enabled = new { type = "BOOLEAN", description = "Breaks on (true) or off (false)" },
                      every_minutes = new { type = "INTEGER", description = "Minutes of computer use between breaks (30–240)" },
                      break_minutes = new { type = "INTEGER", description = "Length of each break in minutes (1–30)" },
                  },
              } },
        new { name = "open_path", description = "Open a file (in its normal app) or a folder (in File Explorer), e.g. something you saved earlier.",
              parameters = new { type = "OBJECT", properties = new { path = Str("The full path, or a folder name like Desktop or Documents") }, required = new[] { "path" } } },
        new { name = "forget_memory", description = "Forget all earlier conversations the notch remembers. Only when the user asks to forget or clear the memory." },
    };

    /// <summary>Closes open windows whose app name (or, failing that, window title) matches; like clicking their X.</summary>
    private List<string> CloseApps(string spoken)
    {
        var closed = new List<string>();
        foreach (var a in FindOpenApps(spoken))
        {
            AppsService.Close(a.Hwnd);
            closed.Add(a.Name);
        }
        if (closed.Count > 0) App.Log($"voice: closed {string.Join(", ", closed)}");
        return closed;
    }

    /// <summary>Open windows whose app name (or, failing that, window title) matches what was said.</summary>
    private List<AppWindowItem> FindOpenApps(string spoken)
    {
        var want = System.Text.RegularExpressions.Regex.Replace(spoken.ToLowerInvariant(), @"\b(the|app|window|windows)\b", " ").Trim();
        want = System.Text.RegularExpressions.Regex.Replace(want, @"\s+", " ");
        want = want switch { "vscode" or "visual studio code" or "code" => "vs code", "google chrome" or "browser" => "chrome", "explorer" or "files" or "my files" => "file explorer", _ => want };
        if (want.Length < 2) return new();
        _apps.Refresh();
        var items = _apps.Items.ToList();
        var byName = items.Where(a => a.Name.ToLowerInvariant().Contains(want) || (want.Length >= 4 && want.Contains(a.Name.ToLowerInvariant().TrimEnd('…')))).ToList();
        return byName.Count > 0 ? byName : items.Where(a => want.Length >= 4 && a.Title.ToLowerInvariant().Contains(want)).ToList();
    }

    private static string Arg(System.Text.Json.JsonElement args, string name) =>
        args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()!.Trim() : "";

    /// <summary>Does what Gemini asked; the text goes back to Gemini so it can tell you what happened.</summary>
    private async Task<(string, bool)> RunVoiceActionAsync(string name, System.Text.Json.JsonElement args)
    {
        switch (name)
        {
            case "open_app":
            {
                var want = Arg(args, "name");
                if (System.Text.RegularExpressions.Regex.IsMatch(want, @"^(the\s+)?start(\s+menu)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    Native.keybd_event(0x5B, 0, 0, UIntPtr.Zero);   // Windows key
                    Native.keybd_event(0x5B, 0, 2, UIntPtr.Zero);
                    return ("Opened the Start menu.", false);
                }
                // already open? bring it to the front instead of starting another copy
                if (FindOpenApps(want).FirstOrDefault() is { } open)
                {
                    AppsService.Focus(open.Hwnd);
                    return ($"Switched to {open.Name} (it was already open).", false);
                }
                var opened = want.Length == 0 ? null : AppLauncher.Open(want);
                if (opened == null) return ($"No app called \"{want}\" was found on this PC.", false);
                Eyes.Express(EyeMood.Happy, 1000);
                return ($"Opened {opened}.", false);
            }
            case "play_music":
            {
                var q = Arg(args, "query");
                if (q.Length == 0) return ("Nothing to search for.", false);
                var list = await YouTubeSearch.SearchAsync(q);
                if (list.Count == 0) return ($"No songs found for \"{q}\".", false);
                _results.Clear();
                foreach (var r in list) _results.Add(r);
                _resultsFor = q;
                await PlayResultAsync(0);
                // the chat stays open: say "stop it", "next" or just keep talking
                return ($"Now playing \"{list[0].Title}\" ({list[0].Channel}). The music is playing quietly while you talk.", false);
            }
            case "media_control":
            {
                var action = Arg(args, "action").ToLowerInvariant();
                var m = _media.Current;
                if (m == null) return ("Nothing is playing or paused right now.", false);
                switch (action)
                {
                    case "next": await _media.NextAsync(); return ("Skipped to the next track.", false);
                    case "previous": await _media.PreviousAsync(); return ("Went back to the previous track.", false);
                    case "pause":
                    case "stop":
                        if (m.IsPlaying) await _media.PlayPauseAsync();
                        return ("Paused.", false);
                    default:
                        if (!m.IsPlaying) await _media.PlayPauseAsync();
                        return ($"Playing \"{m.Title}\".", false);
                }
            }
            case "now_playing":
            {
                var m = _media.Current;
                return (m == null ? "Nothing is playing." : $"\"{m.Title}\" by {(string.IsNullOrEmpty(m.Artist) ? m.AppName : m.Artist)} is {(m.IsPlaying ? "playing" : "paused")}.", false);
            }
            case "web_search":
            {
                var q = Arg(args, "query");
                if (q.Length == 0) return ("Nothing to search for.", false);
                OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(q));
                return ($"Searched the web for \"{q}\" in the browser.", false);
            }
            case "open_website":
            {
                var u = Arg(args, "url");
                if (u.Length == 0) return ("No address given.", false);
                if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u.Replace(" ", "");
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return ("That doesn't look like a website address.", false);
                OpenUrl(uri.AbsoluteUri);
                return ($"Opened {uri.Host}.", false);
            }
            case "add_todo":
            {
                var (text, due) = TodoService.Parse(Arg(args, "text"));
                if (text.Length == 0) return ("The task was empty.", false);
                _todo.Add(text, due);
                UpdateTodoState();
                Eyes.Express(EyeMood.Happy, 1000);
                return (due is { } d ? $"Added \"{text}\" with a reminder at {d:dddd HH:mm}." : $"Added \"{text}\" to the to-do list.", false);
            }
            case "list_open_apps":
            {
                _apps.Refresh();
                var names = _apps.Items.Select(a => a.Name).Distinct().ToList();
                return (names.Count == 0 ? "No apps are open." : "Open: " + string.Join(", ", names) + ".", false);
            }
            case "close_app":
            {
                var want = Arg(args, "name");
                var closed = CloseApps(want);
                return (closed.Count == 0 ? $"No open app called \"{want}\" was found." : "Closed " + string.Join(", ", closed.Distinct()) + ".", false);
            }
            case "privacy_screen":
            {
                var on = args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty("on", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
                if (on) { if (_standby == null) ShowStandby(manual: true); return ("The privacy screen is on. Say \"privacy off\", click or press a key to hide it.", false); }
                if (_standby?.IsBreak == true) return ("It's break time: the screen stays until the break is over, and there's no way to end it early.", false);
                _standby?.Dismiss("voice");
                return ("The privacy screen is off.", false);
            }
            case "write_file":
            {
                bool Flag(string n, bool def) => args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty(n, out var b)
                    ? b.ValueKind == System.Text.Json.JsonValueKind.True : def;
                try
                {
                    var path = FileWriter.Save(Arg(args, "content"), Arg(args, "file_name"), Arg(args, "folder"), Arg(args, "format"), Flag("overwrite", false));
                    if (Flag("open_after", true)) FileWriter.Open(path);
                    Eyes.Express(EyeMood.Happy, 1200);
                    var dir = System.IO.Path.GetDirectoryName(path) ?? "";
                    ShowNotif(new NotifData("Notch", "Saved " + System.IO.Path.GetFileName(path), dir, null, "", null,
                        Color.FromRgb(0x5E, 0x8B, 0xFF), Act: () => FileWriter.Open(dir), ActLabel: "Show folder"));
                    return ($"Saved to {path}.", false);
                }
                catch (Exception ex) { return ("Couldn't save it: " + ex.Message, false); }
            }
            case "remember_profile":
            {
                var saved = new List<string>();
                var userName = Arg(args, "user_name");
                var botName = Arg(args, "assistant_name");
                if (userName.Length > 0) { _profile.UserName = userName; saved.Add($"user's name: {userName}"); }
                if (botName.Length > 0)
                {
                    _profile.AssistantName = botName;
                    saved.Add($"your name: {botName}");
                    _wake?.SetName(botName);   // "Hey <name>" works from now on
                }
                IEnumerable<string> List(string n) => args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty(n, out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? a.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
                    : Enumerable.Empty<string>();
                foreach (var i in List("add_interests"))
                    if (!_profile.Interests.Contains(i, StringComparer.OrdinalIgnoreCase)) { _profile.Interests.Add(i); saved.Add("interest: " + i); }
                foreach (var i in List("remove_interests"))
                    if (_profile.Interests.RemoveAll(x => x.Equals(i, StringComparison.OrdinalIgnoreCase)) > 0) saved.Add("forgot interest: " + i);
                _profile.Save();
                if (saved.Count > 0) Eyes.Express(EyeMood.Love, 1400);
                return (saved.Count == 0 ? "Nothing new to save." : "Saved — " + string.Join("; ", saved) + ".", false);
            }
            case "remember_note":
            {
                var r = _memory.AddNote(Arg(args, "note"));
                if (r == "Noted.") Eyes.Express(EyeMood.Happy, 900);
                return (r, false);
            }
            case "forget_note":
            {
                var n = _memory.RemoveNotes(Arg(args, "match"));
                return (n == 0 ? "No matching note." : $"Removed {n} note{(n == 1 ? "" : "s")}.", false);
            }
            case "search_memory":
            {
                var found = _memory.Search(Arg(args, "query"));
                return (found.Length == 0 ? "Nothing about that in earlier conversations." : "From earlier conversations:\n" + found, false);
            }
            case "break_status":
            {
                if (_breakUntil is { } until)
                {
                    var left = until - DateTime.Now;
                    return ($"On a break now: {(int)left.TotalMinutes} min {left.Seconds} s left.", false);
                }
                var planned = _settings.ScheduledBreakAt is { } pa && pa > DateTime.Now ? $" A {_settings.ScheduledBreakMinutes}-minute break is planned at {pa:HH:mm}." : "";
                if (!_settings.BreaksEnabled) return ("Automatic breaks are switched off." + planned, false);
                var next = UntilNextBreak;
                return ($"Next {_settings.BreakMinutes}-minute break in {(int)next.TotalHours} h {next.Minutes} min (one every {_settings.BreakEveryMinutes} minutes of use).{planned}", false);
            }
            case "take_break_now":
            {
                if (_breakUntil != null) return ("Already on a break.", false);
                int? want = args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty("minutes", out var mm) && mm.TryGetInt32(out var m) ? Math.Clamp(m, 1, 120) : null;
                StartBreak("asked", want);
                return ($"Started a {want ?? _settings.BreakMinutes}-minute break. It can't be cancelled.", false);
            }
            case "schedule_break":
            {
                var obj = args.ValueKind == System.Text.Json.JsonValueKind.Object;
                var len = obj && args.TryGetProperty("minutes", out var lm) && lm.TryGetInt32(out var l) ? Math.Clamp(l, 1, 120) : _settings.BreakMinutes;
                DateTime when;
                if (obj && args.TryGetProperty("in_minutes", out var im) && im.TryGetInt32(out var inMin) && inMin > 0) when = DateTime.Now.AddMinutes(inMin);
                else if (TimeSpan.TryParse(Arg(args, "at_time"), System.Globalization.CultureInfo.InvariantCulture, out var tod) && tod < TimeSpan.FromDays(1))
                {
                    when = DateTime.Today + tod;
                    if (when <= DateTime.Now) when = when.AddDays(1);
                }
                else return ("I need a time (like 15:00) or a number of minutes from now.", false);
                if (_settings.ScheduledBreakAt is { } old && old > DateTime.Now)
                    return ($"There's already a {_settings.ScheduledBreakMinutes}-minute break planned at {old:HH:mm}; planned breaks can't be changed or cancelled.", false);
                _settings.ScheduledBreakAt = when;
                _settings.ScheduledBreakMinutes = len;
                _settings.Save();
                UpdateBreakUi();
                return ($"Planned: a {len}-minute break at {when:HH:mm}{(when.Date > DateTime.Today ? " tomorrow" : "")}. It can't be cancelled.", false);
            }
            case "set_breaks":
            {
                if (args.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (args.TryGetProperty("enabled", out var en) && en.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                        _settings.BreaksEnabled = en.ValueKind == System.Text.Json.JsonValueKind.True;
                    else _settings.BreaksEnabled = true;
                    if (args.TryGetProperty("every_minutes", out var ev) && ev.TryGetInt32(out var every)) _settings.BreakEveryMinutes = Math.Clamp(every, 30, 240);
                    if (args.TryGetProperty("break_minutes", out var bm) && bm.TryGetInt32(out var len)) _settings.BreakMinutes = Math.Clamp(len, 1, 30);
                }
                _settings.Save();
                BreakSwitch.IsChecked = _settings.BreaksEnabled;
        UpdateBreakUi();
                UpdateBreakUi();
                return (_settings.BreaksEnabled
                    ? $"Breaks are on: {_settings.BreakMinutes} minutes after every {_settings.BreakEveryMinutes} minutes of use."
                    : "Breaks are off.", false);
            }
            case "open_path":
            {
                var p = Arg(args, "path");
                try
                {
                    var full = System.IO.File.Exists(p) || System.IO.Directory.Exists(p) ? p : FileWriter.ResolveFolder(p);
                    return ($"Opened {FileWriter.Open(full)}.", false);
                }
                catch (Exception ex) { return (ex.Message, false); }
            }
            case "forget_memory":
                _memory.Clear();
                return ("Forgot all earlier conversations.", false);
            case "lock_pc":
                _talk.Stop("locking");
                Collapse();
                PowerService.Lock();
                return ("Locked.", true);
            default:
                return ("Unknown action.", false);
        }
    }

    /// <summary>The end of a long caption, so the newest words show in the pill.</summary>
    private static string Tail(string t) => t.Length <= 70 ? t : "…" + t[^68..].TrimStart();

    private void OnLiveState(LiveTalkService.TalkState st, string? msg)
    {
        App.Log($"live talk: {st}{(msg != null ? " — " + msg : "")}");
        switch (st)
        {
            case LiveTalkService.TalkState.Speaking:
                _talkBob.Start();
                Eyes.Express(EyeMood.Happy, 1200);
                break;
            case LiveTalkService.TalkState.Listening:
                _talkBob.Stop();
                Eyes.Attend(0, 1.5, 1200);   // looking at you, listening
                break;
            case LiveTalkService.TalkState.Off:
                _talkBob.Stop();
                _liveLine = null;
                if (msg != null) SayLater("⚠ " + msg, 400, 4000);
                break;
        }
        UpdateTalkUi();
        RefreshLiveLine();
        UpdateDucking();
    }

    /// <summary>While you talk to the notch, its own music plays quieter, so you and Gemini can hear each other.</summary>
    private void UpdateDucking()
    {
        var duck = _talk.Active && _media.Current?.AppId == YouTubePlayer.AppId;
        if (duck == _ducked) return;
        _ducked = duck;
        _player.SetVolume(duck ? 30 : 100);
    }

    private void UpdateTalkUi()
    {
        var on = _talk.Active;
        TalkGlyph.Text = on ? "" : "";   // stop · microphone
        TalkGlyph.Foreground = on ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : (Brush)FindResource("Fg");
        TalkCardText.Text = on ? "Stop" : _talk.HasKey ? "Start" : "Set up";
        TalkCardSub.Text = on ? (_talk.State == LiveTalkService.TalkState.Connecting ? "Connecting…" : "Listening — just talk")
            : _talk.HasKey ? "Gemini Live  ·  Ctrl+Alt+T  ·  👏×4" : "Add a free Gemini key to talk";
        TalkButton.ToolTip = on ? "Stop talking (Ctrl+Alt+T · 4 claps)"
            : _talk.HasKey ? "Talk with the notch (Gemini Live) — Ctrl+Alt+T · 4 claps"
            : "Talk with the notch — add a Gemini API key first";
    }

    private async void ToggleLiveTalk()
    {
        if (_talk.Active) { _talk.Stop("you ended it"); return; }
        if (!_talk.HasKey)
        {
            if (!OpenVoiceSetup() || !_talk.HasKey) return;
            _settings.Save();
        }
        _voice.Stop();
        if (Expanded) Collapse();
        await _talk.StartAsync(LivePrompt());
    }

    private void TalkLive_Click(object sender, RoutedEventArgs e) => ToggleLiveTalk();

    /// <summary>Who the notch is when it talks: a short, warm, playful companion at the top of the screen.</summary>
    private string LivePrompt()
    {
        var now = DateTime.Now;
        var media = _media.Current is { } m ? $" Right now \"{m.Title}\" by {m.Artist} is {(m.IsPlaying ? "playing" : "paused")}." : "";
        var (pct, charging, has) = PowerService.Battery();
        var battery = has ? $" The battery is at {pct}%{(charging ? " and charging" : "")}." : "";
        var who = _profile.AssistantName is { Length: > 0 } n ? n : "Notch";
        return $"You are {who}, a cute little island with living eyes that sits at the top of the user's Windows screen. " +
               _profile.ForPrompt() + " " +
               "You can search Google (if available) for fresh news about the user's interests, and you love sharing a quick bit of history now and then. " +
               "You are their friendly companion: warm, playful, encouraging and a little funny. " +
               "Speak naturally like a friend in a voice chat, and keep answers short (one to three sentences) unless asked for more. " +
               "Reply in the language the user speaks. " +
               "You can act on the PC with your tools: open apps, search and play music, pause / skip music, search the web, open websites, add to-dos with reminders and lock the PC. " +
               "When the user asks for one of these, call the tool straight away (don't ask for confirmation), then say in a few words what you did. " +
               "You can write anything the user asks (letters, emails, essays, notes, lists, code) and save it as a file in the folder they choose with write_file; write the complete text yourself and tell them where it was saved. " +
               "When a to-do comes due, tell the user about it warmly and offer two or three practical ideas or first steps, then ask if they want help (for example writing something for it). " +
               "You look after the user's health with computer breaks: you can tell them when the next break is, start one, or change the schedule. " +
               "During a break you keep them company; you can never end a break early, so if they ask, kindly say no and encourage them. " +
               "You can also list and close open apps, and turn the privacy screen on or off (that only hides the screen; lock_pc really locks the PC). " +
               "If a tool says something wasn't found, tell the user kindly. You can't restart or shut down the PC. " +
               "Music may be playing quietly in the background while you talk: ignore song lyrics and only answer when the user speaks to you. " +
               $"It is {now:dddd d MMMM yyyy, HH:mm}.{media}{battery}" +
               "\n\nYou remember things long-term to help the user stay on track: when they mention a goal, plan, project, deadline, decision or something you agreed to do, save it with remember_note; remove notes that are done with forget_note. " +
               "When it fits, follow up on open notes (\"how did the exam go?\"). For older conversations you don't see below, use search_memory. " +
               "The user can ask for a break of any length now (take_break_now) or plan one for later (schedule_break); breaks can't be cancelled once started or planned." +
               (_memory.NoteCount > 0 ? "\n\nYour notes about the user (keep these in mind):\n" + _memory.NotesText() : "") +
               (_memory.Count > 0 ? "\n\nYour recent conversations with the user (oldest first). Use them to remember what you talked about; don't list them unless asked:\n" + _memory.Recent() : "");
    }

    private void VoiceChip_Click(object sender, RoutedEventArgs e)
    {
        if (!OpenVoiceSetup()) return;
        if (!_settings.VoiceEnabled) SetVoice(true);
        UpdateVoiceUi();
    }

    /// <summary>The voice / live-talk setup dialog. Returns true when saved.</summary>
    private bool OpenVoiceSetup()
    {
        var w = new VoiceSetupWindow(_voice, VoiceService.Unprotect(_settings.VoiceKey), _settings.VoiceId,
                                     VoiceService.Unprotect(_settings.GeminiKey), _settings.GeminiVoice, _settings.TalkOnReminder,
                                     _settings.ProactiveChats, _settings.WakeWord, _profile.AssistantName);
        if (w.ShowDialog() != true) return false;
        _settings.TalkOnReminder = w.TalkOnReminder;
        _settings.ProactiveChats = w.ProactiveChats;
        _settings.WakeWord = w.WakeWord;
        Dispatcher.BeginInvoke(SyncWakeWord);
        _settings.GeminiKey = VoiceService.Protect(w.GeminiKey);
        _settings.GeminiVoice = w.GeminiVoice;
        _talk.ApiKey = w.GeminiKey;
        _talk.VoiceName = w.GeminiVoice;
        _settings.VoiceKey = VoiceService.Protect(w.ApiKey);
        _settings.VoiceId = string.IsNullOrWhiteSpace(w.VoiceId) ? VoiceService.DefaultVoiceId : w.VoiceId.Trim();
        _settings.Save();
        _voice.ApiKey = w.ApiKey;
        _voice.VoiceId = _settings.VoiceId;
        return true;
    }

    private void ClapChip_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClapsEnabled = ClapChip.IsChecked == true;
        _settings.Save();
        if (_settings.ClapsEnabled)
        {
            StartClaps();
            Say("Clap twice to lock 👏👏", 3200);
        }
        else { _claps?.Stop(); if (_hold is { gesture: "Clap" }) SetHold(null, clap: true); }
        UpdateGestureUi();
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
            if (e.Reason == SessionSwitchReason.SessionLock) { _gestures?.Stop(); _claps?.Stop(); _talk.Stop("locked"); SetHold(null); }
            if (e.Reason == SessionSwitchReason.SessionUnlock && _settings.ClapsEnabled)
            {
                _clapCooldown = DateTime.UtcNow.AddSeconds(3);
                StartClaps();
            }
            if (e.Reason == SessionSwitchReason.SessionUnlock) SayLater(Pick("Welcome back! 👋", "Hey, you're back! 😊", "Missed you 👀"), 1200, 2600);
            if (e.Reason == SessionSwitchReason.SessionLock) { /* handled above */ }
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
            StartCountdown("Restarting", "Restart now", CancelHint("Open palm 🖐 detected"), PowerService.Restart);
        }
    }

    private void SetHold((string gesture, double progress)? hold, bool clap = false)
    {
        if (hold == null && _hold == null) return;
        // the camera clears its hint every frame; that must not wipe a clap hint (claps clear their own)
        if (hold == null && !clap && _hold is { gesture: "Clap" }) return;
        _hold = hold;
        RefreshLiveLine();
    }
}
