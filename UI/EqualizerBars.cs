using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WindowsNotch.UI;

/// <summary>Animated equalizer bars filled with the live accent gradient.</summary>
public sealed class EqualizerBars : StackPanel
{
    private readonly List<ScaleTransform> _scales = new();
    private bool _playing, _built;
    private static readonly Random Rnd = new();

    public double BarHeight { get; set; } = 14;
    public double BarWidth { get; set; } = 3;
    public int Bars { get; set; } = 5;

    public EqualizerBars()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Loaded += (_, _) => Build();
    }

    private void Build()
    {
        if (_built) return;
        _built = true;
        Height = BarHeight;
        for (int i = 0; i < Bars; i++)
        {
            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = BarHeight,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Margin = new Thickness(i == 0 ? 0 : BarWidth * 0.7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1),
            };
            var st = new ScaleTransform(1, 0.2);
            bar.RenderTransform = st;
            bar.SetResourceReference(Shape.FillProperty, "AccentGradient");
            Children.Add(bar);
            _scales.Add(st);
        }
        Apply();
    }

    public bool IsPlaying
    {
        get => _playing;
        set { if (_playing == value) return; _playing = value; Apply(); }
    }

    private void Apply()
    {
        if (!_built) return;
        foreach (var st in _scales)
        {
            if (!_playing)
            {
                st.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0.2, TimeSpan.FromMilliseconds(420)) { EasingFunction = Motion.Spring(0.6) });
                continue;
            }
            // Each bar gets its own random "rhythm" so it never looks mechanical.
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var t = 0.0;
            for (int i = 0; i < 10; i++)
            {
                t += 0.11 + Rnd.NextDouble() * 0.17;
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.18 + Rnd.NextDouble() * 0.82,
                    KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), Motion.InOut));
            }
            t += 0.16;
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), Motion.InOut));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }
}
