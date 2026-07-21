using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace SoulsTracker.Desktop;

/// <summary>Dark hex input paired with the Windows color palette dialog.</summary>
public sealed class ColorField : Grid
{
    private readonly Border swatch;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(string),
        typeof(ColorField),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public ColorField()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        System.Windows.Controls.TextBox hexBox = new()
        {
            MinWidth = 110,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5, 3, 4, 3)
        };
        hexBox.SetBinding(
            System.Windows.Controls.TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(Value))
            {
                Source = this,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        Children.Add(hexBox);

        System.Windows.Controls.Button button = new()
        {
            MinWidth = 32,
            Padding = new Thickness(7, 5, 7, 5),
            ToolTip = "Choose color"
        };
        AutomationProperties.SetName(button, "Open native color palette");
        swatch = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(184, 192, 204)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        button.Content = swatch;
        button.Click += (_, _) => OpenNativeColorDialog();
        SetColumn(button, 1);
        Children.Add(button);

        UpdateSwatch(Value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs eventArgs) =>
        ((ColorField)target).UpdateSwatch((string?)eventArgs.NewValue);

    private void OpenNativeColorDialog()
    {
        Forms.ColorDialog dialog = new()
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true,
            Color = ToDrawingColor(Parse(Value) ?? Colors.White)
        };

        Window? owner = Window.GetWindow(this);
        IntPtr ownerHandle = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
        Forms.DialogResult result = ownerHandle == IntPtr.Zero
            ? dialog.ShowDialog()
            : dialog.ShowDialog(new NativeWindowOwner(ownerHandle));

        if (result == Forms.DialogResult.OK)
        {
            Value = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }
    }

    private void UpdateSwatch(string? value) =>
        swatch.Background = new SolidColorBrush(Parse(value) ?? Colors.Transparent);

    private static System.Windows.Media.Color? Parse(string? text)
    {
        if (text is not { Length: 7 } || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit)) return null;
        return System.Windows.Media.Color.FromRgb(
            Convert.ToByte(text[1..3], 16),
            Convert.ToByte(text[3..5], 16),
            Convert.ToByte(text[5..7], 16));
    }

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color color) =>
        System.Drawing.Color.FromArgb(color.R, color.G, color.B);

    private sealed class NativeWindowOwner(IntPtr handle) : Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
