using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace WindowsNotch.UI;

/// <summary>Small animation toolkit so the code-behind reads like a motion spec.</summary>
public static class Motion
{
    public static IEasingFunction Spring(double amplitude = 0.3) => new BackEase { Amplitude = amplitude, EasingMode = EasingMode.EaseOut };
    public static IEasingFunction Out => new CubicEase { EasingMode = EasingMode.EaseOut };
    public static IEasingFunction OutQuint => new QuinticEase { EasingMode = EasingMode.EaseOut };
    public static IEasingFunction InOut => new SineEase { EasingMode = EasingMode.EaseInOut };

    public static void To(IAnimatable target, DependencyProperty dp, double to, int ms,
        IEasingFunction? ease = null, int delay = 0, double? from = null, Action? done = null)
    {
        var a = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, ms)),
            EasingFunction = ease ?? Out,
            BeginTime = TimeSpan.FromMilliseconds(delay),
        };
        if (from.HasValue) a.From = from;
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(dp, a);
    }

    public static void Color(IAnimatable target, DependencyProperty dp, Color to, int ms) =>
        target.BeginAnimation(dp, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = InOut });

    /// <summary>Fade + optional blur-in, the "morph" feel of the Dynamic Island.</summary>
    public static void Show(UIElement el, int ms = 260, int delay = 0, double blur = 10, double fromY = 0)
    {
        el.Visibility = Visibility.Visible;
        To(el, UIElement.OpacityProperty, 1, ms, Out, delay);
        if (blur > 0)
        {
            var b = new BlurEffect { Radius = blur, RenderingBias = RenderingBias.Performance };
            el.Effect = b;
            To(b, BlurEffect.RadiusProperty, 0, ms + 60, Out, delay, done: () => { if (el.Effect == b) el.Effect = null; });
        }
        if (fromY != 0)
        {
            var t = new TranslateTransform(0, fromY);
            el.RenderTransform = t;
            To(t, TranslateTransform.YProperty, 0, ms + 120, Spring(0.25), delay);
        }
    }

    public static void Hide(UIElement el, int ms = 140, double blur = 8)
    {
        if (blur > 0)
        {
            var b = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
            el.Effect = b;
            To(b, BlurEffect.RadiusProperty, blur, ms);
        }
        To(el, UIElement.OpacityProperty, 0, ms, Out, done: () =>
        {
            if (el.Opacity < 0.01) { el.Visibility = Visibility.Collapsed; el.Effect = null; }
        });
    }

    /// <summary>Text swap: slide the new text up into place.</summary>
    public static void SlideIn(UIElement el, double fromY = 9, int ms = 320, int delay = 0)
    {
        var t = el.RenderTransform as TranslateTransform ?? new TranslateTransform();
        el.RenderTransform = t;
        To(t, TranslateTransform.YProperty, 0, ms, Spring(0.35), delay, from: fromY);
        To(el, UIElement.OpacityProperty, 1, ms - 60, Out, delay, from: 0);
    }

    public static void Pop(ScaleTransform s, double from, double to, int ms = 420, double amp = 0.45)
    {
        To(s, ScaleTransform.ScaleXProperty, to, ms, Spring(amp), from: from);
        To(s, ScaleTransform.ScaleYProperty, to, ms, Spring(amp), from: from);
    }

    public static void Scale(ScaleTransform s, double to, int ms = 380, double amp = 0.3)
    {
        To(s, ScaleTransform.ScaleXProperty, to, ms, Spring(amp));
        To(s, ScaleTransform.ScaleYProperty, to, ms, Spring(amp));
    }
}
