using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WindowsNotch.UI;

public enum EyeMood { Neutral, Happy, Surprised, Sleepy, Sad, Love, Angry }

/// <summary>
/// A pair of small 3D eyes for the collapsed notch: shaded eyeballs with a moving, foreshortening iris,
/// a fixed specular highlight, real eyelids, and moods. They blink, glance around, follow the mouse,
/// bob to music and react when poked (click), winked at (double-click) or stared at (hover).
/// </summary>
public sealed class NotchEyes : Grid
{
    private const double D = 12.5, Gap = 7;      // eyeball diameter / distance between eyes
    private static readonly Color Lid = Color.FromRgb(0x17, 0x17, 0x1C);   // close to the frosted pill's dark glass

    private readonly Eye _left, _right;
    private readonly TranslateTransform _bob = new(), _shake = new();
    private readonly ScaleTransform _pop = new(1, 1);
    private readonly DispatcherTimer _life = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly Random _rng = new();
    private readonly DropShadowEffect _glow;
    private readonly Ellipse _blushL, _blushR;
    private DateTime _nextBlink, _nextGlance, _moodUntil = DateTime.MinValue, _attentionUntil = DateTime.MinValue, _hoverSince;
    private EyeMood _mood = EyeMood.Neutral, _baseMood = EyeMood.Neutral;
    private bool _dancing, _hover;
    private double _lookX, _lookY;
    private readonly Queue<DateTime> _pokes = new();

    public NotchEyes()
    {
        // generous transparent hit area so the eyes are easy to hover / poke without opening the notch
        Width = 64;
        Height = 30;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _pop;
        ToolTip = null;

        var face = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        face.RenderTransform = new TransformGroup { Children = { _bob, _shake } };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        _left = new Eye(this, isLeft: true);
        _right = new Eye(this, isLeft: false) { Margin = new Thickness(Gap, 0, 0, 0) };
        row.Children.Add(_left);
        row.Children.Add(_right);
        face.Children.Add(row);

        // eyebrows: they carry most of the expression
        var brows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center };
        _browL = Brow(isLeft: true, out _browLRot, out _browLY);
        _browR = Brow(isLeft: false, out _browRRot, out _browRY);
        _browR.Margin = new Thickness(Gap, 0, 0, 0);
        brows.Children.Add(_browL);
        brows.Children.Add(_browR);
        face.Children.Add(brows);

        // a little sparkle that twinkles now and then
        _sparkle = new Path
        {
            Data = Geometry.Parse("M 3,0 Q 3.4,2.6 6,3 Q 3.4,3.4 3,6 Q 2.6,3.4 0,3 Q 2.6,2.6 3,0 Z"),
            Fill = Brushes.White, Width = 6, Height = 6, Stretch = Stretch.Fill, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, -4, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _sparkleScale,
        };
        face.Children.Add(_sparkle);

        // cheeks that blush when you hover
        _blushL = Blush(-1);
        _blushR = Blush(1);
        face.Children.Add(_blushL);
        face.Children.Add(_blushR);
        Children.Add(face);

        _glow = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.55, Color = AccentTheme.Default };
        face.Effect = _glow;

        _nextBlink = DateTime.Now.AddSeconds(1.5);
        _nextGlance = DateTime.Now.AddSeconds(2.5);
        _life.Tick += (_, _) => Live();
        IsVisibleChanged += (_, _) => { if (IsVisible) _life.Start(); else _life.Stop(); };
        Loaded += (_, _) => _life.Start();

        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => SetHover(false);
        MouseLeftButtonDown += OnClick;
    }

    private readonly Path _browL, _browR, _sparkle;
    private readonly RotateTransform _browLRot, _browRRot;
    private readonly TranslateTransform _browLY, _browRY;
    private readonly ScaleTransform _sparkleScale = new(0.2, 0.2);
    private DateTime _nextSparkle = DateTime.Now.AddSeconds(5);

    private static Path Brow(bool isLeft, out RotateTransform rot, out TranslateTransform y)
    {
        rot = new RotateTransform();
        y = new TranslateTransform();
        return new Path
        {
            Data = Geometry.Parse("M 0,2.6 Q 4.6,-0.4 9.4,2.2"),
            Stroke = new SolidColorBrush(Color.FromArgb(0xE6, 0xF2, 0xF4, 0xF8)), StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = D, Height = 4, Stretch = Stretch.None, Margin = new Thickness(1.5, 0, 0, 0),
            RenderTransformOrigin = new Point(isLeft ? 0.7 : 0.3, 0.5),
            RenderTransform = new TransformGroup { Children = { new ScaleTransform(isLeft ? 1 : -1, 1), rot, y } },
            Opacity = 0.9,
        };
    }

    /// <summary>Eyebrow pose per mood: (lift px, inner-end tilt °, opacity).</summary>
    private void PoseBrows(EyeMood mood, int ms)
    {
        var (lift, tilt, op) = mood switch
        {
            EyeMood.Surprised => (-2.6, 0.0, 1.0),
            EyeMood.Happy => (-1.2, -6.0, 1.0),
            EyeMood.Love => (-1.6, -8.0, 1.0),
            EyeMood.Sad => (-0.6, -18.0, 0.95),
            EyeMood.Angry => (1.2, 20.0, 1.0),
            EyeMood.Sleepy => (1.4, 4.0, 0.55),
            _ => (_hover ? -1.3 : 0.0, 0.0, 0.9),
        };
        Motion.To(_browLY, TranslateTransform.YProperty, lift, ms, Motion.Spring(0.4));
        Motion.To(_browRY, TranslateTransform.YProperty, lift, ms, Motion.Spring(0.4));
        // positive tilt pulls the inner ends down (angry); negative raises them (sad / soft)
        Motion.To(_browLRot, RotateTransform.AngleProperty, tilt, ms, Motion.Spring(0.4));
        Motion.To(_browRRot, RotateTransform.AngleProperty, -tilt, ms, Motion.Spring(0.4));
        Motion.To(_browL, OpacityProperty, op, ms);
        Motion.To(_browR, OpacityProperty, op, ms);
    }

    private static Ellipse Blush(int side) => new()
    {
        Width = 7, Height = 3.4, Opacity = 0, IsHitTestVisible = false,
        Fill = new RadialGradientBrush(Color.FromArgb(0xB0, 0xFF, 0x5C, 0x8A), Color.FromArgb(0, 0xFF, 0x5C, 0x8A)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
        RenderTransform = new TranslateTransform(side * (D / 2 + Gap / 2 + 1), 3.5),
    };

    /// <summary>The soft glow the theme tints with the artwork colour.</summary>
    public DropShadowEffect Glow => _glow;

    /// <summary>Raised when the user pokes / winks at the eyes (so the host can e.g. avoid expanding).</summary>
    public event Action? Interacted;

    /// <summary>What just happened to the eyes: "hover", "poke", "angry", "wink" — the notch answers with a speech bubble.</summary>
    public event Action<string>? Reaction;

    /// <summary>Iris colour (follows the artwork accent).</summary>
    public void SetIris(Color c, int ms = 600)
    {
        _left.SetIris(c, ms);
        _right.SetIris(c, ms);
    }

    // ------------------------------------------------------------------ idle life
    private void Live()
    {
        var now = DateTime.Now;
        if (_mood != _baseMood && now >= _moodUntil) SetMood(_baseMood, 260);

        if (now >= _nextBlink)
        {
            if (_mood is EyeMood.Neutral or EyeMood.Sleepy or EyeMood.Sad or EyeMood.Surprised) Blink(_rng.NextDouble() < 0.18);
            _nextBlink = now.AddMilliseconds(_rng.Next(2200, 5600));
        }
        if (_hover && now - _hoverSince > TimeSpan.FromSeconds(1.3) && _mood == EyeMood.Neutral)
            Express(EyeMood.Happy, 1500);

        if (now >= _nextSparkle)
        {
            _nextSparkle = now.AddSeconds(_rng.Next(6, 14));
            if (_mood is not (EyeMood.Sleepy or EyeMood.Sad or EyeMood.Angry))
            {
                var tw = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
                tw.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new BackEase { EasingMode = EasingMode.EaseOut }));
                tw.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
                _sparkleScale.BeginAnimation(ScaleTransform.ScaleXProperty, tw);
                _sparkleScale.BeginAnimation(ScaleTransform.ScaleYProperty, tw);
                var op = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
                _sparkle.BeginAnimation(OpacityProperty, op);
            }
        }

        if (now >= _nextGlance && now >= _attentionUntil && !_hover)
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
        if (twice) { _left.Blink(250); _right.Blink(250); }
    }

    /// <summary>Points the irises: dx in -4..4, dy in -3..3.</summary>
    public void Look(double dx, double dy, int ms = 380)
    {
        _lookX = Math.Clamp(dx, -4, 4);
        _lookY = Math.Clamp(dy, -3, 3);
        _left.Look(_lookX, _lookY, ms);
        _right.Look(_lookX, _lookY, ms);
    }

    /// <summary>Look towards something (e.g. the mouse) for a moment.</summary>
    public void Attend(double dx, double dy, int holdMs = 900)
    {
        Look(dx, dy, 220);
        _attentionUntil = DateTime.Now.AddMilliseconds(holdMs);
        _nextGlance = _attentionUntil;
    }

    // ------------------------------------------------------------------ moods & reactions
    public void SetBaseMood(EyeMood mood)
    {
        _baseMood = mood;
        if (DateTime.Now >= _moodUntil) SetMood(mood, 400);
    }

    public void Express(EyeMood mood, int ms = 1600)
    {
        SetMood(mood, 200);
        _moodUntil = DateTime.Now.AddMilliseconds(ms);
    }

    private void SetMood(EyeMood mood, int ms)
    {
        _mood = mood;
        _left.Apply(mood, ms);
        _right.Apply(mood, ms);
        PoseBrows(mood, ms + 80);
    }

    /// <summary>Little hop (notification, plug-in…).</summary>
    public void Jump(double scale = 1.25)
    {
        Motion.Pop(_pop, 0.85, 1, 520, 0.6);
        var up = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(560) };
        up.KeyFrames.Add(new EasingDoubleKeyFrame(-3.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        up.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(560)), new BounceEase { Bounces = 2, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
        up.Completed += (_, _) => { if (_dancing) SetDancing(true); };
        _bob.BeginAnimation(TranslateTransform.YProperty, up);
        _left.Widen(scale);
        _right.Widen(scale);
    }

    /// <summary>Small nod (each volume step).</summary>
    public void Nod(int dir)
    {
        Look(_lookX, dir * 2.4, 120);
        _attentionUntil = DateTime.Now.AddMilliseconds(700);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        t.Tick += (_, _) => { t.Stop(); Look(_lookX, 0, 300); };
        t.Start();
    }

    public void SetDancing(bool on)
    {
        _dancing = on;
        if (on)
            _bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.6, -1.2, TimeSpan.FromMilliseconds(430))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
        else
            Motion.To(_bob, TranslateTransform.YProperty, 0, 250);
    }

    // ------------------------------------------------------------------ interaction
    private void SetHover(bool on)
    {
        _hover = on;
        _hoverSince = DateTime.Now;
        _left.Dilate(on ? 1.25 : 1);
        _right.Dilate(on ? 1.25 : 1);
        Motion.To(_blushL, OpacityProperty, on ? 1 : 0, 350);
        Motion.To(_blushR, OpacityProperty, on ? 1 : 0, 350);
        if (!on && _mood == EyeMood.Happy) Express(EyeMood.Neutral, 10);
        PoseBrows(_mood, 260);   // brows lift a little when you look at them
        if (on) Reaction?.Invoke("hover");
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Interacted?.Invoke();
        if (e.ClickCount >= 2) { Wink(); return; }

        // poke: squeeze shut + wiggle, then a surprised look
        var now = DateTime.Now;
        _pokes.Enqueue(now);
        while (_pokes.Count > 0 && now - _pokes.Peek() > TimeSpan.FromSeconds(2.2)) _pokes.Dequeue();

        _left.Squeeze();
        _right.Squeeze();
        var wiggle = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        foreach (var (x, t) in new[] { (-2.2, 60), (2.2, 130), (-1.6, 200), (1.2, 270), (0.0, 420) })
            wiggle.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
        _shake.BeginAnimation(TranslateTransform.XProperty, wiggle);
        Motion.Pop(_pop, 0.82, 1, 480, 0.6);

        if (_pokes.Count >= 4) { Express(EyeMood.Angry, 2200); _pokes.Clear(); Reaction?.Invoke("angry"); }
        else
        {
            Reaction?.Invoke("poke");
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(330) };
            t.Tick += (_, _) => { t.Stop(); Express(EyeMood.Surprised, 650); };
            t.Start();
        }
    }

    public void Wink()
    {
        _right.WinkClose();
        Express(EyeMood.Love, 1300);
        Motion.Pop(_pop, 0.9, 1, 420, 0.5);
        Reaction?.Invoke("wink");
    }

    // ------------------------------------------------------------------ one eyeball
    private sealed class Eye : Grid
    {
        private readonly NotchEyes _owner;
        private readonly bool _isLeft;
        private readonly Grid _iris;
        private readonly TranslateTransform _irisPos = new();
        private readonly ScaleTransform _irisSquash = new(1, 1);
        private readonly ScaleTransform _pupilSize = new(1, 1);
        private readonly GradientStop _irisIn, _irisOut;
        private readonly Ellipse _pupil;
        private readonly Path _heart;
        private readonly Border _upper, _lower;
        private readonly TranslateTransform _upperY = new(0, -D), _lowerY = new(0, D);
        private readonly RotateTransform _upperTilt = new();
        private readonly ScaleTransform _ballSize = new(1, 1);
        private double _upperOpen = -D;

        public Eye(NotchEyes owner, bool isLeft)
        {
            _owner = owner;
            _isLeft = isLeft;
            Width = D;
            Height = D;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = _ballSize;
            Clip = new EllipseGeometry(new Point(D / 2, D / 2), D / 2, D / 2);

            // sclera: lit from the upper-left, darker toward the rim → reads as a sphere
            Children.Add(new Ellipse
            {
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0xFF, 0xFF), 0), new(Color.FromRgb(0xEE, 0xF1, 0xF6), 0.5),
                    new(Color.FromRgb(0xB4, 0xBD, 0xCB), 0.92), new(Color.FromRgb(0x8E, 0x98, 0xA8), 1),
                }) { GradientOrigin = new Point(0.36, 0.3), Center = new Point(0.44, 0.4), RadiusX = 0.62, RadiusY = 0.62 },
            });

            // iris + pupil (moves; squashes toward the edges like a real sphere)
            _irisIn = new GradientStop(AccentTheme.Lighter(AccentTheme.Default, 0.35), 0);
            _irisOut = new GradientStop(Darker(AccentTheme.Default, 0.45), 1);
            _iris = new Grid
            {
                Width = 7.6, Height = 7.6,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { _irisSquash, _irisPos } },
            };
            _iris.Children.Add(new Ellipse
            {
                Fill = new RadialGradientBrush(new GradientStopCollection { _irisIn, _irisOut }),
                Stroke = new SolidColorBrush(Color.FromArgb(0x8C, 0x05, 0x0A, 0x14)), StrokeThickness = 0.7,
            });
            _pupil = new Ellipse
            {
                Width = 3.6, Height = 3.6, Fill = new SolidColorBrush(Color.FromRgb(0x06, 0x07, 0x0A)),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _pupilSize,
            };
            _iris.Children.Add(_pupil);
            _heart = new Path
            {
                Data = Geometry.Parse("M 4.5,9 C 0,5.5 0,1.5 2.5,1.2 C 3.6,1 4.3,1.8 4.5,2.6 C 4.7,1.8 5.4,1 6.5,1.2 C 9,1.5 9,5.5 4.5,9 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0x71)), Stretch = Stretch.Uniform,
                Width = 8.4, Height = 8.4, Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.5, 0.5),
            };
            _iris.Children.Add(_heart);
            Children.Add(_iris);

            // specular highlight: fixed in place while the iris moves under it → 3D
            Children.Add(new Ellipse
            {
                Width = 3.6, Height = 2.6, Fill = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(3, 2.6, 0, 0), RenderTransform = new RotateTransform(-25), IsHitTestVisible = false,
            });
            Children.Add(new Ellipse
            {
                Width = 1.5, Height = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 3.2, 3.2), IsHitTestVisible = false,
            });

            // eyelids (same colour as the notch so they read as skin)
            _upper = new Border
            {
                Width = D + 6, Height = D + 1, Background = new SolidColorBrush(Lid),
                CornerRadius = new CornerRadius(0, 0, D / 2 + 3, D / 2 + 3),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                RenderTransformOrigin = new Point(isLeft ? 0.8 : 0.2, 1),
                RenderTransform = new TransformGroup { Children = { _upperTilt, _upperY } },
            };
            _lower = new Border
            {
                Width = D + 6, Height = D, Background = new SolidColorBrush(Lid),
                CornerRadius = new CornerRadius(D / 2 + 3, D / 2 + 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = _lowerY,
            };
            Children.Add(_upper);
            Children.Add(_lower);
        }

        private static Color Darker(Color c, double t) => Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

        public void SetIris(Color c, int ms)
        {
            Motion.Color(_irisIn, GradientStop.ColorProperty, AccentTheme.Lighter(c, 0.35), ms);
            Motion.Color(_irisOut, GradientStop.ColorProperty, Darker(c, 0.45), ms);
        }

        public void Look(double dx, double dy, int ms)
        {
            // iris slides across the ball and flattens toward the rim (foreshortening)
            Motion.To(_irisPos, TranslateTransform.XProperty, dx * 0.78, ms, Motion.Spring(0.3));
            Motion.To(_irisPos, TranslateTransform.YProperty, dy * 0.85, ms, Motion.Spring(0.3));
            Motion.To(_irisSquash, ScaleTransform.ScaleXProperty, 1 - Math.Abs(dx) / 4 * 0.2, ms, Motion.Out);
            Motion.To(_irisSquash, ScaleTransform.ScaleYProperty, 1 - Math.Abs(dy) / 3 * 0.16, ms, Motion.Out);
        }

        public void Dilate(double s) => Motion.Scale(_pupilSize, s, 320, 0.4);

        public void Blink(int delayMs)
        {
            var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(delayMs) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(_upperOpen, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            _upperY.BeginAnimation(TranslateTransform.YProperty, a);
        }

        public void Squeeze()
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(_upperOpen, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            _upperY.BeginAnimation(TranslateTransform.YProperty, a);
            var b = new DoubleAnimationUsingKeyFrames();
            b.KeyFrames.Add(new EasingDoubleKeyFrame(D * 0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
            b.KeyFrames.Add(new EasingDoubleKeyFrame(D * 0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
            b.KeyFrames.Add(new EasingDoubleKeyFrame(D, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
            _lowerY.BeginAnimation(TranslateTransform.YProperty, b);
        }

        public void WinkClose()
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(_upperOpen, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(820))));
            _upperY.BeginAnimation(TranslateTransform.YProperty, a);
        }

        public void Widen(double s)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(s, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(s, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1050)), new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }));
            _ballSize.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            _ballSize.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        public void Apply(EyeMood mood, int ms)
        {
            // upper lid: how far it stays down; lower lid: how far it rises; tilt for sad / angry
            (double upper, double lower, double tilt) = mood switch
            {
                EyeMood.Sleepy => (-D * 0.5, D, 0.0),
                EyeMood.Sad => (-D * 0.62, D, _isLeft ? -20.0 : 20.0),
                EyeMood.Angry => (-D * 0.6, D, _isLeft ? 22.0 : -22.0),
                EyeMood.Happy => (-D, D * 0.42, 0.0),
                EyeMood.Love => (-D, D * 0.7, 0.0),
                _ => (-D, D, 0.0),
            };
            _upperOpen = upper;
            _upperY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(upper, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.Out });
            _lowerY.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(lower, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.Out });
            _upperTilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(tilt, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Motion.Out });

            var love = mood == EyeMood.Love;
            Motion.To(_heart, OpacityProperty, love ? 1 : 0, ms);
            Motion.To(_pupil, OpacityProperty, love ? 0 : 1, ms);
            if (love) Motion.Pop((ScaleTransform)_heart.RenderTransform, 0.4, 1, 600, 0.7);

            Motion.Scale(_pupilSize, mood switch { EyeMood.Surprised => 0.65, EyeMood.Angry => 0.75, EyeMood.Love => 1, _ => _owner._hover ? 1.25 : 1 }, 280, 0.3);
            if (mood == EyeMood.Surprised) Widen(1.18);
            Motion.Color((SolidColorBrush)((Ellipse)_iris.Children[0]).Stroke, SolidColorBrush.ColorProperty,
                mood == EyeMood.Angry ? Color.FromArgb(0xE0, 0xFF, 0x2D, 0x2D) : Color.FromArgb(0x8C, 0x05, 0x0A, 0x14), ms);
        }
    }
}
