using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WindowsNotch.UI;

/// <summary>
/// One live accent colour (taken from the current artwork) shared by every gradient, glow and
/// equalizer bar. Changing it cross-fades all of them at once.
/// </summary>
public sealed class AccentTheme
{
    public static readonly Color Default = System.Windows.Media.Color.FromRgb(0x30, 0xD1, 0x58);

    private readonly SolidColorBrush _solid, _soft, _faint;
    private readonly GradientStop _vTop, _vBottom, _hLeft, _hRight, _lineMid, _ambTop, _washA, _washB;
    private readonly List<DropShadowEffect> _glows = new();
    public Color Current { get; private set; } = Default;

    /// <param name="target">A window's resources. (Application-level resources get frozen by WPF and can't animate.)</param>
    public AccentTheme(ResourceDictionary target)
    {
        _solid = new SolidColorBrush(Default);
        _soft = new SolidColorBrush(WithAlpha(Default, 0x40));
        _faint = new SolidColorBrush(WithAlpha(Default, 0x1C));

        _vTop = new GradientStop(Lighter(Default, 0.14), 0);
        _vBottom = new GradientStop(Default, 1);
        var vertical = new LinearGradientBrush(new GradientStopCollection { _vTop, _vBottom }, new Point(0, 0), new Point(0, 1));

        _hLeft = new GradientStop(Default, 0);
        _hRight = new GradientStop(Lighter(Default, 0.12), 1);
        var horizontal = new LinearGradientBrush(new GradientStopCollection { _hLeft, _hRight }, new Point(0, 0), new Point(1, 0));

        // thin glowing line: transparent → accent → transparent
        _lineMid = new GradientStop(Default, 0.5);
        var line = new LinearGradientBrush(new GradientStopCollection
        {
            new(Colors.Transparent, 0), _lineMid, new(Colors.Transparent, 1),
        }, new Point(0, 0), new Point(1, 0));

        // radial "aurora" used behind the expanded panel when there is no artwork
        _ambTop = new GradientStop(WithAlpha(Default, 0x55), 0);
        var aurora = new RadialGradientBrush(new GradientStopCollection { _ambTop, new(Colors.Transparent, 1) })
        {
            Center = new Point(0.25, 0), GradientOrigin = new Point(0.25, 0), RadiusX = 0.9, RadiusY = 1.3,
        };

        // diagonal wash of the artwork colour over the frosted open notch
        _washA = new GradientStop(WithAlpha(Default, 0x66), 0);
        _washB = new GradientStop(WithAlpha(Darker(Default, 0.5), 0x33), 0.55);
        var wash = new LinearGradientBrush(new GradientStopCollection { _washA, _washB, new(Colors.Transparent, 1) }, new Point(0, 0), new Point(1, 1));

        var r = target;
        r["AccentWash"] = wash;
        r["Accent"] = _solid;
        r["AccentSoft"] = _soft;
        r["AccentFaint"] = _faint;
        r["AccentGradient"] = vertical;
        r["AccentGradientH"] = horizontal;
        r["AccentLine"] = line;
        r["AccentAurora"] = aurora;
    }

    public void RegisterGlow(DropShadowEffect e)
    {
        e.Color = Current;
        _glows.Add(e);
    }

    public event Action<Color, int>? Changed;

    public void Set(Color c, int ms = 700)
    {
        if (c == Current) return;
        Current = c;
        Changed?.Invoke(c, ms);
        Motion.Color(_solid, SolidColorBrush.ColorProperty, c, ms);
        Motion.Color(_soft, SolidColorBrush.ColorProperty, WithAlpha(c, 0x40), ms);
        Motion.Color(_faint, SolidColorBrush.ColorProperty, WithAlpha(c, 0x1C), ms);
        Motion.Color(_vTop, GradientStop.ColorProperty, Lighter(c, 0.14), ms);
        Motion.Color(_vBottom, GradientStop.ColorProperty, c, ms);
        Motion.Color(_hLeft, GradientStop.ColorProperty, c, ms);
        Motion.Color(_hRight, GradientStop.ColorProperty, Lighter(c, 0.12), ms);
        Motion.Color(_lineMid, GradientStop.ColorProperty, c, ms);
        Motion.Color(_ambTop, GradientStop.ColorProperty, WithAlpha(c, 0x55), ms);
        Motion.Color(_washA, GradientStop.ColorProperty, WithAlpha(c, 0x66), ms);
        Motion.Color(_washB, GradientStop.ColorProperty, WithAlpha(Darker(c, 0.5), 0x33), ms);
        foreach (var g in _glows) Motion.Color(g, DropShadowEffect.ColorProperty, c, ms);
    }

    public static Color Darker(Color c, double t) => System.Windows.Media.Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

    public static Color WithAlpha(Color c, byte a) => System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Lighter(Color c, double t) => System.Windows.Media.Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
}
