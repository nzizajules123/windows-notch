using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsNotch;

/// <summary>Compact iOS-style battery glyph with percentage.</summary>
public sealed class BatteryIndicator : StackPanel
{
    private static readonly Brush Outline = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Normal = Brushes.White;
    private static readonly Brush Low = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush Charging = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));

    private readonly TextBlock _pct = new()
    {
        FontSize = 11.5, FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
    };
    private readonly Border _fill = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5),
        Margin = new Thickness(1.5), Width = 10,
    };
    private readonly TextBlock _bolt = new()
    {
        Text = "", FontSize = 8, Foreground = Brushes.Black,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    public BatteryIndicator()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _bolt.FontFamily = (FontFamily)Application.Current.Resources["IconFont"];

        var inner = new Grid();
        inner.Children.Add(_fill);
        inner.Children.Add(_bolt);
        var body = new Border
        {
            Width = 22, Height = 11, CornerRadius = new CornerRadius(3.2),
            BorderThickness = new Thickness(1.1), BorderBrush = Outline, Child = inner,
        };
        var nub = new Border
        {
            Width = 1.8, Height = 4.5, Background = Outline, CornerRadius = new CornerRadius(0, 1, 1, 0),
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
        _pct.Text = $"{percent}%";
        _fill.Width = Math.Max(2, 16.8 * percent / 100.0);
        _fill.Background = charging ? Charging : percent <= 20 ? Low : Normal;
        _bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        ToolTip = charging ? $"{percent}% · charging" : $"{percent}% · on battery";
    }
}
