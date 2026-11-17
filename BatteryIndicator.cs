using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace WindowsNotch;

/// <summary>
/// Compact battery: percentage + a glassy cell whose fill is coloured by level
/// (white → amber → red), turns green with a moving shimmer and a bolt while charging,
/// and gently pulses when low.
/// </summary>
public sealed class BatteryIndicator : StackPanel
{
    private const double CellW = 23, CellH = 11.5, Inset = 1.6;
    private const double FillMax = CellW - 2 * Inset - 2.4;

    private static readonly Brush Outline = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FillNormal = Grad("#FFFFFFFF", "#FFD3D9E3");
    private static readonly Brush FillMid = Grad("#FFFFE27A", "#FFFFB020");
    private static readonly Brush FillLow = Grad("#FFFF7A6E", "#FFFF3B30");
    private static readonly Brush FillCharge = Grad("#FF7CF29C", "#FF30D158");
    private static readonly Brush TextNormal = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0xF5, 0xF5, 0xF7)));
    private static readonly Brush TextLow = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4F)));
    private static readonly Brush TextCharge = Frozen(new SolidColorBrush(Color.FromRgb(0x5E, 0xE0, 0x82)));

    private readonly TextBlock _pct = new()
    {
        FontSize = 11, FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
    };
    private readonly Border _fill = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2.2),
        Margin = new Thickness(Inset), Width = FillMax, ClipToBounds = true,
    };
    private readonly Rectangle _shimmer = new() { Width = 8, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _shimmerX = new(-10, 0);
    private readonly Path _bolt = new()
    {
        Data = Geometry.Parse("M 5.6,0 L 0.6,6.6 L 3.9,6.6 L 3.1,11 L 8.2,4.2 L 4.8,4.2 Z"),
        Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x3D, 0x1C)), StrokeThickness = 0.9,
        Width = 6.4, Height = 9, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly DropShadowEffect _glow = new() { BlurRadius = 8, ShadowDepth = 0, Opacity = 0, Color = Color.FromRgb(0x30, 0xD1, 0x58) };
    private int _lastPct = -1;
    private bool _lastCharging, _lowPulse;

    public BatteryIndicator()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;

        _shimmer.Fill = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0, 255, 255, 255), 0), new(Color.FromArgb(0xB0, 255, 255, 255), 0.5), new(Color.FromArgb(0, 255, 255, 255), 1),
        }, 0);
        _shimmer.RenderTransform = _shimmerX;
        _shimmer.Visibility = Visibility.Collapsed;
        _fill.Child = _shimmer;
        _fill.Effect = _glow;

        var inner = new Grid();
        inner.Children.Add(_fill);
        inner.Children.Add(_bolt);
        var body = new Border
        {
            Width = CellW, Height = CellH, CornerRadius = new CornerRadius(3.8),
            BorderThickness = new Thickness(1.1), BorderBrush = Outline, Child = inner,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        };
        var nub = new Border
        {
            Width = 1.8, Height = 4.2, Background = Outline, CornerRadius = new CornerRadius(0, 1.2, 1.2, 0),
            Margin = new Thickness(1, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_pct);
        Children.Add(body);
        Children.Add(nub);
    }

    public void Update(int percent, bool charging, bool hasBattery)
    {
        Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
        if (!hasBattery) return;
        percent = Math.Clamp(percent, 0, 100);
        _pct.Text = $"{percent}%";
        ToolTip = charging ? $"{percent}% · charging" : $"{percent}% · on battery";

        var w = Math.Max(2.2, FillMax * percent / 100.0);
        if (_lastPct < 0) _fill.Width = w;
        else if (percent != _lastPct) _fill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        var low = !charging && percent <= 20;
        _fill.Background = charging ? FillCharge : low ? FillLow : percent <= 35 ? FillMid : FillNormal;
        _pct.Foreground = charging ? TextCharge : low ? TextLow : TextNormal;
        _bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;

        if (charging != _lastCharging || _lastPct < 0)
        {
            if (charging)
            {
                _shimmer.Visibility = Visibility.Visible;
                _shimmerX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-10, FillMax + 2, TimeSpan.FromSeconds(1.6))
                { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                _glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.35, 0.9, TimeSpan.FromSeconds(1.2))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                _glow.Color = Color.FromRgb(0x30, 0xD1, 0x58);
            }
            else
            {
                _shimmer.Visibility = Visibility.Collapsed;
                _shimmerX.BeginAnimation(TranslateTransform.XProperty, null);
                _glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                _glow.Opacity = 0;
            }
        }

        if (low != _lowPulse)
        {
            _lowPulse = low;
            _fill.BeginAnimation(OpacityProperty, low
                ? new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }
        _lastPct = percent;
        _lastCharging = charging;
    }

    private static Brush Grad(string top, string bottom)
    {
        var b = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
        b.Freeze();
        return b;
    }

    private static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
}
