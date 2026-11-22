using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsNotch.Services;

namespace WindowsNotch.UI;

/// <summary>
/// macOS-lock-screen-style standby clock: your desktop heavily blurred, the date and a big time, sleepy eyes,
/// and a frosted "now playing" card. Shown after a while away (or on demand); any mouse move / key closes it.
/// (Windows' real lock screen runs on a protected desktop no app can draw on — this is the closest thing.)
/// </summary>
public sealed class StandbyWindow : Window
{
    private readonly MediaService _media;
    private readonly TextBlock _time = new(), _date = new(), _battery = new(), _title = new(), _artist = new();
    private readonly Border _card = new();
    private readonly ImageBrush _art = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _progress = new(), _track = new();
    private readonly TextBlock _playGlyph = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NotchEyes _eyes = new();
    private Point? _mouseStart;
    private bool _closing;
    private readonly bool _manual;
    private readonly DateTime _shownAt = DateTime.Now;
    // synced lyrics in the middle of the screen: previous · current (big) · next
    private readonly Func<LyricsResult?>? _lyrics;
    private readonly TextBlock _lyPrev = new(), _lyNow = new(), _lyNext = new();
    private readonly StackPanel _lyBox = new();
    private readonly DispatcherTimer _lyTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _lyIdx = int.MinValue;

    // break mode: the screen stays until the break is over (emergency: hold Ctrl+Shift+E for 5 s)
    private readonly DateTime? _breakUntil;
    private readonly TextBlock _breakLeft = new(), _breakTip = new(), _breakWarn = new();
    private readonly StackPanel _breakBox = new();
    private bool _allowClose;
    private DateTime _lastEscape = DateTime.MinValue, _emergencySince = DateTime.MaxValue;
    private int _tipIndex;
    private static readonly string[] Tips =
    {
        "Stand up and stretch your arms and back 🧘",
        "Look at something far away for 20 seconds — your eyes will thank you 👀",
        "Drink a glass of water 💧",
        "Roll your shoulders and gently turn your neck",
        "Take five slow, deep breaths 😌",
        "Walk around the room for a minute 🚶",
        "Open a window and get some fresh air 🌿",
    };
    /// <summary>Someone clicked / pressed a key to leave the break early (time left).</summary>
    public event Action<TimeSpan>? EscapeAttempt;
    /// <summary>The break was ended with the emergency keys.</summary>
    public event Action? EmergencyExit;
    public bool IsBreak => _breakUntil != null;

    /// <param name="manual">opened on purpose as a privacy screen: mouse moves don't close it, only a click or a key</param>
    /// <param name="lyrics">the notch's synced lyrics for what's playing (shown big in the middle)</param>
    /// <param name="breakUntil">a computer break: can't be closed before this time</param>
    public StandbyWindow(MediaService media, bool manual = false, Func<LyricsResult?>? lyrics = null, DateTime? breakUntil = null)
    {
        _media = media;
        _breakUntil = breakUntil;
        if (breakUntil != null) { manual = true; lyrics = null; }
        _lyrics = lyrics;
        _manual = manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Left = 0; Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Cursor = Cursors.None;
        Opacity = 0;

        var root = new Grid();

        // 1) the desktop, heavily blurred
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow ?? this);
        var shot = Frost.Capture(0, 0, (int)(Width * dpi.DpiScaleX), (int)(Height * dpi.DpiScaleY));
        root.Children.Add(new Image
        {
            Source = shot, Stretch = Stretch.UniformToFill, Margin = new Thickness(-80),
            Effect = new BlurEffect { Radius = 90, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        });
        root.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x70, 0, 0, 0), 0), new(Color.FromArgb(0x40, 0, 0, 0), 0.45), new(Color.FromArgb(0x99, 0, 0, 0), 1),
            }, 90),
        });

        // 2) date + big time, top centre (macOS style)
        var head = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Height * 0.09, 0, 0) };
        _eyes.HorizontalAlignment = HorizontalAlignment.Center;
        _eyes.IsHitTestVisible = false;
        _eyes.RenderTransform = new ScaleTransform(1.5, 1.5);
        _eyes.RenderTransformOrigin = new Point(0.5, 0.5);
        _eyes.Margin = new Thickness(0, 0, 0, 14);
        head.Children.Add(_eyes);
        _date.FontSize = 24; _date.FontWeight = FontWeights.SemiBold; _date.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        _date.HorizontalAlignment = HorizontalAlignment.Center;
        _date.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        head.Children.Add(_date);
        _time.FontSize = 150; _time.FontWeight = FontWeights.Bold; _time.Margin = new Thickness(0, -14, 0, 0);
        _time.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _time.HorizontalAlignment = HorizontalAlignment.Center;
        _time.Foreground = new LinearGradientBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF), 90);
        _time.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.25 };
        head.Children.Add(_time);
        root.Children.Add(head);

        // 3) now playing (frosted card)
        _card.Width = 380; _card.CornerRadius = new CornerRadius(24); _card.Padding = new Thickness(14);
        _card.Background = new SolidColorBrush(Color.FromArgb(0x52, 0x2A, 0x2A, 0x30));
        _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)); _card.BorderThickness = new Thickness(1);
        _card.HorizontalAlignment = HorizontalAlignment.Center; _card.VerticalAlignment = VerticalAlignment.Bottom;
        _card.Margin = new Thickness(0, 0, 0, Height * 0.14);
        _card.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0.35 };
        var cg = new Grid();
        cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cg.ColumnDefinitions.Add(new ColumnDefinition());
        cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cg.Children.Add(new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(14), Background = _art });
        var txt = new StackPanel { Margin = new Thickness(14, 2, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _title.FontSize = 15; _title.FontWeight = FontWeights.SemiBold; _title.Foreground = Brushes.White; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _artist.FontSize = 12.5; _artist.Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)); _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        txt.Children.Add(_title);
        txt.Children.Add(_artist);
        var track = _track;
        track.Height = 4; track.CornerRadius = new CornerRadius(2); track.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)); track.Margin = new Thickness(0, 9, 0, 0);
        _progress.HorizontalAlignment = HorizontalAlignment.Left; _progress.CornerRadius = new CornerRadius(2); _progress.Background = Brushes.White; _progress.Width = 0;
        track.Child = _progress;
        txt.Children.Add(track);
        Grid.SetColumn(txt, 1);
        cg.Children.Add(txt);
        var play = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
        };
        _playGlyph.FontFamily = (FontFamily)Application.Current.Resources["IconFont"]; _playGlyph.FontSize = 16; _playGlyph.Foreground = Brushes.White;
        _playGlyph.HorizontalAlignment = HorizontalAlignment.Center; _playGlyph.VerticalAlignment = VerticalAlignment.Center;
        play.Child = _playGlyph;
        play.MouseLeftButtonUp += async (_, e) => { e.Handled = true; await _media.PlayPauseAsync(); };
        Grid.SetColumn(play, 2);
        cg.Children.Add(play);
        _card.Child = cg;
        root.Children.Add(_card);

        // 3b) synced lyrics, karaoke-style, between the clock and the card
        _lyBox.HorizontalAlignment = HorizontalAlignment.Center;
        _lyBox.VerticalAlignment = VerticalAlignment.Center;
        _lyBox.Margin = new Thickness(60, Height * 0.16, 60, 0);
        _lyBox.MaxWidth = Math.Min(1100, Width - 120);
        _lyBox.IsHitTestVisible = false;
        _lyBox.Visibility = Visibility.Collapsed;
        var lyFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        foreach (var (tb, size, alpha) in new[] { (_lyPrev, 22.0, (byte)0x66), (_lyNow, 40.0, (byte)0xFF), (_lyNext, 22.0, (byte)0x80) })
        {
            tb.FontFamily = lyFont; tb.FontSize = size; tb.TextAlignment = TextAlignment.Center;
            tb.TextWrapping = TextWrapping.Wrap; tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.FontWeight = tb == _lyNow ? FontWeights.Bold : FontWeights.SemiBold;
            tb.Foreground = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
            tb.Margin = new Thickness(0, tb == _lyPrev ? 0 : 10, 0, 0);
            _lyBox.Children.Add(tb);
        }
        _lyNow.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.45, Color = Colors.White };
        _lyNow.RenderTransform = new TranslateTransform();
        root.Children.Add(_lyBox);

        // 3c) break: countdown, an idea for the break, and a warning when you try to leave early
        if (breakUntil != null)
        {
            _breakBox.HorizontalAlignment = HorizontalAlignment.Center;
            _breakBox.VerticalAlignment = VerticalAlignment.Center;
            _breakBox.Margin = new Thickness(60, Height * 0.18, 60, 0);
            _breakBox.IsHitTestVisible = false;
            var font = new FontFamily("Segoe UI Variable Display, Segoe UI");
            _breakBox.Children.Add(new TextBlock
            {
                Text = "☕  Break time", FontFamily = font, FontSize = 26, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center,
            });
            _breakLeft.FontFamily = font; _breakLeft.FontSize = 64; _breakLeft.FontWeight = FontWeights.Bold;
            _breakLeft.HorizontalAlignment = HorizontalAlignment.Center; _breakLeft.Margin = new Thickness(0, 4, 0, 0);
            _breakLeft.Foreground = new LinearGradientBrush(Color.FromRgb(0x7F, 0xD8, 0xFF), Color.FromRgb(0xB4, 0x8C, 0xFF), 0);
            _breakBox.Children.Add(_breakLeft);
            _breakTip.FontFamily = font; _breakTip.FontSize = 20; _breakTip.TextAlignment = TextAlignment.Center; _breakTip.TextWrapping = TextWrapping.Wrap;
            _breakTip.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)); _breakTip.Margin = new Thickness(0, 10, 0, 0);
            _breakTip.MaxWidth = 760; _breakTip.HorizontalAlignment = HorizontalAlignment.Center;
            _breakBox.Children.Add(_breakTip);
            _breakWarn.FontFamily = font; _breakWarn.FontSize = 16; _breakWarn.FontWeight = FontWeights.SemiBold; _breakWarn.TextAlignment = TextAlignment.Center;
            _breakWarn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40)); _breakWarn.Margin = new Thickness(0, 18, 0, 0);
            _breakWarn.HorizontalAlignment = HorizontalAlignment.Center; _breakWarn.Opacity = 0; _breakWarn.RenderTransform = new TranslateTransform();
            _breakBox.Children.Add(_breakWarn);
            root.Children.Add(_breakBox);
        }

        // 4) battery + hint
        var foot = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 36) };
        _battery.FontSize = 13; _battery.FontWeight = FontWeights.SemiBold; _battery.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        _battery.HorizontalAlignment = HorizontalAlignment.Center;
        foot.Children.Add(_battery);
        foot.Children.Add(new TextBlock
        {
            Text = breakUntil != null ? "The break ends by itself  ·  emergency only: hold Ctrl+Shift+E for 5 seconds"
                 : manual ? "Click or press any key to return" : "Move the mouse or press any key to return", FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center,
        });
        root.Children.Add(foot);
        Content = root;

        _clock.Tick += (_, _) => { Refresh(); BreakTick(); };
        if (breakUntil != null)
        {
            // a break can't be closed early (Alt+F4 included) and stays in front
            Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };
            Deactivated += (_, _) => { if (!_closing) Dispatcher.BeginInvoke(() => { Topmost = false; Topmost = true; Activate(); }); };
        }
        _lyTimer.Tick += (_, _) => RefreshLyrics();
        _media.Changed += Refresh;
        Loaded += (_, _) =>
        {
            Refresh();
            BreakTick();
            _clock.Start();
            if (_lyrics != null) { RefreshLyrics(); _lyTimer.Start(); }
            Activate();
            Focus();
            _eyes.SetBaseMood(EyeMood.Sleepy);
            // entrance: fade in, time drifts down, card rises
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = Motion.Out });
            head.RenderTransform = new TranslateTransform(0, -24);
            Motion.To(head.RenderTransform, TranslateTransform.YProperty, 0, 900, Motion.OutQuint, 100);
            _card.RenderTransform = new TranslateTransform(0, 30);
            Motion.To(_card.RenderTransform, TranslateTransform.YProperty, 0, 900, Motion.OutQuint, 260);
        };
        Closed += (_, _) => { _clock.Stop(); _lyTimer.Stop(); _media.Changed -= Refresh; };

        // a short grace period so the click / shortcut that opened it doesn't close it straight away
        bool Settled() => (DateTime.Now - _shownAt).TotalMilliseconds > 700;
        PreviewKeyDown += (_, e) =>
        {
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_breakUntil != null)
            {
                e.Handled = true;
                // emergency: hold Ctrl+Shift+E for 5 seconds
                var mods = Keyboard.Modifiers;
                if (k == Key.E && mods.HasFlag(ModifierKeys.Control) && mods.HasFlag(ModifierKeys.Shift))
                {
                    if (_emergencySince == DateTime.MaxValue) _emergencySince = DateTime.Now;
                    var held = (DateTime.Now - _emergencySince).TotalSeconds;
                    if (held >= 5) { EmergencyExit?.Invoke(); _allowClose = true; Dismiss("break: emergency exit"); }
                    else Warn($"Emergency exit… keep holding ({Math.Ceiling(5 - held)} s)");
                    return;
                }
                _emergencySince = DateTime.MaxValue;
                if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
                TriedToLeave();
                return;
            }
            if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            e.Handled = true;
            if (Settled()) Dismiss("key " + k);
        };
        PreviewMouseDown += (_, e) =>
        {
            if (e.OriginalSource is Border b && b.Child == _playGlyph) return;
            if (e.OriginalSource is TextBlock tb && tb == _playGlyph) return;
            if (_breakUntil != null) { TriedToLeave(); return; }
            if (Settled()) Dismiss("click");
        };
        PreviewKeyUp += (_, e) => { if (e.Key == Key.E) _emergencySince = DateTime.MaxValue; };
        PreviewMouseMove += (_, e) =>
        {
            if (_manual) return;
            var p = e.GetPosition(this);
            _mouseStart ??= p;
            if ((p - _mouseStart.Value).Length > 12 && Settled()) Dismiss("mouse");
        };
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var pattern = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('h') ? "h:mm" : "HH:mm";
        _time.Text = now.ToString(pattern);
        _date.Text = now.ToString("dddd d MMMM");
        var (pct, charging, has) = PowerService.Battery();
        _battery.Text = has ? $"{pct}%{(charging ? "  ·  charging ⚡" : "")}" : "";

        var m = _media.Current;
        _card.Visibility = m != null ? Visibility.Visible : Visibility.Collapsed;
        if (m == null) return;
        _title.Text = m.Title;
        _artist.Text = string.IsNullOrEmpty(m.Artist) ? m.AppName : m.Artist;
        _art.ImageSource = m.Thumbnail;
        _playGlyph.Text = m.IsPlaying ? "" : "";
        var d = m.Duration.TotalSeconds;
        _progress.Width = d > 0 ? Math.Clamp(m.LivePosition.TotalSeconds / d, 0, 1) * _track.ActualWidth : 0;
        _card.Background = new SolidColorBrush(AccentTheme.WithAlpha(AccentTheme.Darker(m.Accent, 0.55), 0x80));
    }

    /// <summary>Counts the break down, changes the idea every 20 seconds, and closes when the break is over.</summary>
    private void BreakTick()
    {
        if (_breakUntil is not { } until) return;
        var left = until - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            _breakLeft.Text = "0:00";
            _allowClose = true;
            Dismiss("break over");
            return;
        }
        _breakLeft.Text = $"{(int)left.TotalMinutes}:{left.Seconds:00}";
        var tip = (int)((DateTime.Now - _shownAt).TotalSeconds / 20) % Tips.Length;
        if (tip != _tipIndex || _breakTip.Text.Length == 0)
        {
            _tipIndex = tip;
            _breakTip.Text = Tips[tip];
            _breakTip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)));
        }
    }

    private static readonly string[] Warnings =
    {
        "Not yet — the break isn't over 🙂",
        "Nice try! Your eyes and back need this 💙",
        "Still on break. Stretch while you wait 🧘",
        "Hands off the keyboard — you've earned these minutes ☕",
    };
    private int _warnCount;

    private void TriedToLeave()
    {
        var left = (_breakUntil ?? DateTime.Now) - DateTime.Now;
        Warn(Warnings[_warnCount++ % Warnings.Length] + $"  ·  {(int)left.TotalMinutes}:{left.Seconds:00} left");
        if ((DateTime.Now - _lastEscape).TotalSeconds < 8) return;   // don't nag on every key press
        _lastEscape = DateTime.Now;
        EscapeAttempt?.Invoke(left);
    }

    private void Warn(string text)
    {
        _breakWarn.Text = text;
        _breakWarn.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))),
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3800))),
            },
        });
        // a little shake
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        foreach (var (x, ms) in new[] { (-10.0, 60), (9.0, 130), (-6.0, 200), (4.0, 270), (0.0, 420) })
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        _breakWarn.RenderTransform.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    /// <summary>Shows the line being sung, with the one before and after; a new line slides up and fades in.</summary>
    private void RefreshLyrics()
    {
        var m = _media.Current;
        var ly = _lyrics?.Invoke();
        if (m == null || ly == null || !ly.Synced)
        {
            _lyIdx = int.MinValue;
            _lyBox.Visibility = Visibility.Collapsed;
            return;
        }
        var lines = ly.Lines;
        var idx = ly.IndexAt(m.LivePosition);
        _lyBox.Visibility = Visibility.Visible;
        _lyBox.Opacity = m.IsPlaying ? 1 : 0.55;
        if (idx == _lyIdx) return;
        _lyIdx = idx;
        _lyPrev.Text = idx > 0 ? lines[idx - 1].Text : "";
        _lyNow.Text = idx >= 0 ? lines[idx].Text : "♪   ♪   ♪";
        _lyNext.Text = idx + 1 < lines.Count ? lines[idx + 1].Text : "";
        _lyPrev.Visibility = _lyPrev.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _lyNext.Visibility = _lyNext.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _lyNow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = Motion.Out });
        _lyNow.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(520)) { EasingFunction = Motion.OutQuint });
    }

    /// <summary>QA: renders the standby screen to an image without showing the window.</summary>
    public System.Windows.Media.Imaging.BitmapSource RenderPreview()
    {
        Refresh();
        RefreshLyrics();
        BreakTick();
        var root = (FrameworkElement)Content;
        var size = new Size(Width, Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        Refresh();
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        return rtb;
    }

    /// <summary>Fades away and closes.</summary>
    public void Dismiss(string why = "")
    {
        if (_closing) return;
        if (_breakUntil != null && !_allowClose) return;   // breaks only end when the time is up (or by emergency)
        _closing = true;
        App.Log("standby: closing (" + why + ")");
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.Out };
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }
}
