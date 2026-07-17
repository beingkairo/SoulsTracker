using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoulsTracker.Desktop;

/// <summary>Dark, bounded hex input with a functional HSV wheel popover.</summary>
public sealed class ColorField : Grid
{
    private readonly TextBox hexBox;
    private readonly Border swatch;
    private readonly Popup picker = null!;
    private readonly Slider valueSlider;
    private double hue;
    private double saturation;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorField), new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public ColorField()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        hexBox = new TextBox { MinWidth = 110, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3, 4, 3) };
        hexBox.SetBinding(TextBox.TextProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Children.Add(hexBox);
        var button = new Button { Padding = new Thickness(0), ToolTip = "Choose color" };
        AutomationProperties.SetName(button, "Open color wheel");
        swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        button.Content = swatch;
        button.Click += (_, _) => { SyncHsv(Value); picker.IsOpen = !picker.IsOpen; };
        SetColumn(button, 1); Children.Add(button);

        Image wheel = new() { Width = 170, Height = 170, Source = CreateWheel(), Cursor = Cursors.Cross };
        wheel.MouseLeftButtonDown += (_, e) => SelectWheel(e.GetPosition(wheel), wheel.ActualWidth);
        valueSlider = new Slider { Minimum = 0, Maximum = 1, Value = 1, Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.White };
        valueSlider.ValueChanged += (_, _) => UpdateFromHsv();
        var panel = new StackPanel { Margin = new Thickness(12), Width = 190 };
        panel.Children.Add(new TextBlock { Text = "Color", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(wheel);
        panel.Children.Add(valueSlider);
        picker = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = new Border { Background = new SolidColorBrush(Color.FromRgb(30, 34, 42)), BorderBrush = new SolidColorBrush(Color.FromRgb(78, 86, 100)), BorderThickness = new Thickness(1), Child = panel } };
        SyncHsv(Value);
    }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        ColorField field = (ColorField)target;
        field.swatch.Background = new SolidColorBrush(Parse((string?)e.NewValue) ?? Colors.Transparent);
    }

    private void SelectWheel(Point point, double size)
    {
        double radius = size / 2;
        double x = point.X - radius, y = point.Y - radius;
        double distance = Math.Sqrt(x * x + y * y) / radius;
        if (distance > 1) return;
        hue = (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
        saturation = distance;
        UpdateFromHsv();
    }

    private void UpdateFromHsv()
    {
        Value = ToHex(FromHsv(hue, saturation, valueSlider.Value));
    }

    private void SyncHsv(string value)
    {
        Color? color = Parse(value);
        if (color is null) return;
        double r = color.Value.R / 255d, g = color.Value.G / 255d, b = color.Value.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta + 6) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        saturation = max == 0 ? 0 : delta / max;
        valueSlider.Value = max;
    }

    private static BitmapSource CreateWheel()
    {
        const int size = 170; byte[] pixels = new byte[size * size * 4]; double radius = size / 2d;
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            double dx = x - radius, dy = y - radius, sat = Math.Sqrt(dx * dx + dy * dy) / radius;
            int index = (y * size + x) * 4;
            if (sat > 1) continue;
            Color color = FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, sat, 1);
            pixels[index] = color.B; pixels[index + 1] = color.G; pixels[index + 2] = color.R; pixels[index + 3] = 255;
        }
        return BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
    }

    private static Color? Parse(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit)) return null;
        return Color.FromRgb(Convert.ToByte(text[1..3], 16), Convert.ToByte(text[3..5], 16), Convert.ToByte(text[5..7], 16));
    }
    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60 % 2) - 1)), m = v - c; (double r, double g, double b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
