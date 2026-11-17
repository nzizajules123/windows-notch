using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WindowsNotch.UI;

public enum EyeMood { Neutral, Happy, Surprised, Sleepy, Sad, Love }

/// <summary>
/// A tiny pair of living eyes for the collapsed notch: they blink, glance around, follow the mouse,
/// bob to music and pull faces when something happens (notification, volume, Wi-Fi, charging…).
/// </summary>
public sealed class NotchEyes : Grid
{
    private const double EyeW = 9, EyeH = 12, Gap = 7;

    private readonly Eye _left, _right;
    private readonly TranslateTransform _look = new();
    private readonly TranslateTransform _bob = new();
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly DispatcherTimer _life = new();
    private readonly Random _rng = new();
    private readonly DropShadowEffect _glow;
    private DateTime _nextBlink, _nextGlance, _moodUntil = DateTime.MinValue, _attentionUntil = DateTime.MinValue;
    private EyeMood _mood = EyeMood.Neutral, _baseMood = EyeMood.Neutral;
    private bool _dancing;

    public NotchEyes()
    {
        Width = EyeW * 2 + Gap + 8;
        Height = EyeH + 6;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        RenderTransformOrigin = new Point(0.5, 0.5);

        var pair = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _left = new Eye(this, mirrored: false);
        _right = new Eye(this, mirrored: true) { Margin = new Thickness(Gap, 0, 0, 0) };
        pair.Children.Add(_left);
        pair.Children.Add(_right);
        pair.RenderTransform = new TransformGroup { Children = { _look, _bob } };
        Children.Add(pair);

        RenderTransform = _pop;
        _glow = new DropShadowEffect { BlurRadius = 9, ShadowDepth = 0, Opacity = 0.85, Color = AccentTheme.Default };
        Effect = _glow;

        _nextBlink = DateTime.Now.AddSeconds(1.5);
        _nextGlance = DateTime.Now.AddSeconds(2.5);
        _life.Interval = TimeSpan.FromMilliseconds(90);
        _life.Tick += (_, _) => Live();
        Loaded += (_, _) => _life.Start();
        Unloaded += (_, _) => _life.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) _life.Start(); else _life.Stop(); };
    }

    /// <summary>The glow the theme tints with the artwork colour.</summary>
    public DropShadowEffect Glow => _glow;

    // ------------------------------------------------------------------ idle life
    private void Live()
    {
        var now = DateTime.Now;
        if (_mood != _baseMood && now >= _moodUntil) SetMood(_baseMood, 260);

        if (now >= _nextBlink)
        {
            if (_mood is EyeMood.Neutral or EyeMood.Sleepy or EyeMood.Sad) Blink(_rng.NextDouble() < 0.18);
            _nextBlink = now.AddMilliseconds(_rng.Next(2200, 5600));
        }
        if (now >= _nextGlance && now >= _attentionUntil)
        {
            if (_rng.NextDouble() < 0.45) Look(0, 0);
            else Look(_rng.Next(-3, 4), _rng.NextDouble() < 0.3 ? _rng.Next(-1, 2) : 0);
            _nextGlance = now.AddMilliseconds(_rng.Next(1600, 4200));
        }
    }

    public void Blink(bool twice = false)
    {
        _left.Blink(0);
        _right.Blink(0);
        if (twice) { _left.Blink(260); _right.Blink(260); }
    }

    /// <summary>Moves the pupils/eyes; dx in -4..4, dy in -2..2.</summary>
    public void Look(double dx, double dy, int ms = 380)
    {
        Motion.To(_look, TranslateTransform.XProperty, Math.Clamp(dx, -4, 4), ms, Motion.Spring(0.35));
        Motion.To(_look, TranslateTransform.YProperty, Math.Clamp(dy, -2, 2), ms, Motion.Spring(0.35));
    }

    /// <summary>Look towards a screen point (e.g. the mouse) for a moment.</summary>
    public void Attend(double dx, double dy, int holdMs = 900)
    {
        Look(dx, dy, 260);
        _attentionUntil = DateTime.Now.AddMilliseconds(holdMs);
        _nextGlance = _attentionUntil;
    }

    // ------------------------------------------------------------------ moods & reactions
    /// <summary>Default face when nothing is happening (e.g. Sleepy on low battery).</summary>
    public void SetBaseMood(EyeMood mood)
    {
        _baseMood = mood;
        if (DateTime.Now >= _moodUntil) SetMood(mood, 400);
    }

    /// <summary>Shows a face for a while, then returns to the base mood.</summary>
    public void Express(EyeMood mood, int ms = 1600)
    {
        SetMood(mood, 220);
        _moodUntil = DateTime.Now.AddMilliseconds(ms);
    }

    private void SetMood(EyeMood mood, int ms)
    {
        _mood = mood;
        _left.Apply(mood, ms);
        _right.Apply(mood, ms);
    }

    /// <summary>Quick hop (notification, gesture…).</summary>
    public void Jump(double scale = 1.35)
    {
        Motion.Pop(_pop, 0.85, 1, 520, 0.6);
        var up = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        up.KeyFrames.Add(new EasingDoubleKeyFrame(-3.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        up.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new BounceEase { Bounces = 2, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
        _bob.BeginAnimation(TranslateTransform.YProperty, up);
        _left.Widen(scale);
        _right.Widen(scale);
        if (_dancing) Dispatcher.BeginInvoke(() => SetDancing(true), DispatcherPriority.Background);
    }

    /// <summary>Small nod, e.g. for each volume step.</summary>
    public void Nod(int dir)
    {
        var a = new DoubleAnimation(dir * 1.6, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = Motion.Spring(0.5) };
        _look.BeginAnimation(TranslateTransform.YProperty, a);
        _attentionUntil = DateTime.Now.AddMilliseconds(700);
    }

    /// <summary>Bob gently to the music while something is playing.</summary>
    public void SetDancing(bool on)
    {
        _dancing = on;
        if (on)
            _bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.6, -1.2, TimeSpan.FromMilliseconds(430))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
        else
            Motion.To(_bob, TranslateTransform.YProperty, 0, 250);
    }

    // ------------------------------------------------------------------ one eye
    private sealed class Eye : Grid
    {
        private readonly NotchEyes _owner;
        private readonly bool _mirrored;
        private readonly Border _ball;
        private readonly ScaleTransform _lid = new(1, 1);   // blink / sleepy (scaleY)
        private readonly ScaleTransform _size = new(1, 1);  // surprise widen
        private readonly Path _arc;                          // ^ happy
        private readonly Path _heart;                        // ♥ love
        private readonly Border _sadLid;                     // slanted lid for sad

        public Eye(NotchEyes owner, bool mirrored)
        {
            _owner = owner;
            _mirrored = mirrored;
            Width = EyeW + 2;
            Height = EyeH + 4;

            _ball = new Border
            {
                Width = EyeW, Height = EyeH, CornerRadius = new CornerRadius(EyeW / 2),
                Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD8, 0xE2, 0xF0), 90),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { _lid, _size } },
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            // tiny highlight → feels alive
            _ball.Child = new Ellipse
            {
                Width = 2.6, Height = 2.6, Fill = new SolidColorBrush(Color.FromArgb(0xC0, 0x9F, 0xB4, 0xD6)),
                HorizontalAlignment = mirrored ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2.2, 2, 0),
            };

            _arc = new Path
            {
                Data = Geometry.Parse("M 0.8,7 Q 4.5,0.5 8.2,7"),
                Stroke = Brushes.White, StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Width = EyeW, Height = 8, Opacity = 0,
            };
            _heart = new Path
            {
                Data = Geometry.Parse("M 4.5,9 C 0,5.5 0,1.5 2.5,1.2 C 3.6,1 4.3,1.8 4.5,2.6 C 4.7,1.8 5.4,1 6.5,1.2 C 9,1.5 9,5.5 4.5,9 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x7A)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Width = EyeW, Height = 10, Stretch = Stretch.Uniform, Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.6, 0.6),
            };
            _sadLid = new Border
            {
                Width = EyeW + 3, Height = 5, Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, Margin = new Thickness(0, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new RotateTransform(mirrored ? -16 : 16),
            };
            Children.Add(_ball);
            Children.Add(_sadLid);
            Children.Add(_arc);
            Children.Add(_heart);
        }

        public void Blink(int delayMs)
        {
            var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
            var open = _owner._mood == EyeMood.Sleepy ? 0.42 : 1.0;
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(open, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            _lid.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        public void Widen(double s)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(s, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(s, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }));
            _size.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            _size.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        public void Apply(EyeMood mood, int ms)
        {
            var ballOn = mood is EyeMood.Neutral or EyeMood.Surprised or EyeMood.Sleepy or EyeMood.Sad;
            Motion.To(_ball, OpacityProperty, ballOn ? 1 : 0, ms);
            Motion.To(_arc, OpacityProperty, mood == EyeMood.Happy ? 1 : 0, ms);
            Motion.To(_heart, OpacityProperty, mood == EyeMood.Love ? 1 : 0, ms);
            Motion.To(_sadLid, OpacityProperty, mood == EyeMood.Sad ? 1 : 0, ms);
            if (mood == EyeMood.Love) Motion.Pop((ScaleTransform)_heart.RenderTransform, 0.4, 1, 600, 0.7);
            if (mood == EyeMood.Happy) Motion.SlideIn(_arc, 3, ms + 150);

            var lid = mood == EyeMood.Sleepy ? 0.42 : 1;
            _lid.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(lid, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.Out });
            if (mood == EyeMood.Surprised) Widen(1.3);
        }
    }
}
