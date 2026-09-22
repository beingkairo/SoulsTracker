using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace SoulsTracker.Desktop;

/// <summary>Bounded whole-unit editing without coercing invalid draft text.</summary>
public sealed class AppearanceNumberField : Grid, IDataErrorInfo
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(AppearanceNumberField), new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(int), typeof(AppearanceNumberField), new PropertyMetadata(0));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(AppearanceNumberField), new PropertyMetadata(100));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(AppearanceNumberField), new PropertyMetadata("px"));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Error => this[nameof(Value)];
    public string this[string columnName] => columnName == nameof(Value) && (!int.TryParse(Value, out int number) || number < Minimum || number > Maximum) ? RangeHelp : "";
    private string RangeHelp => $"Enter a whole number from {Minimum} to {Maximum} {Unit}.";
    private readonly TextBox input;

    public AppearanceNumberField()
    {
        Width = 150;
        Height = 32;
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Gray, Background = new SolidColorBrush(Color.FromRgb(25, 29, 35)) };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        frame.Child = layout;
        Children.Add(frame);
        input = new TextBox { MinWidth = 0, BorderThickness = new Thickness(0), Padding = new Thickness(5, 3, 4, 3), VerticalContentAlignment = VerticalAlignment.Center };
        input.SetBinding(TextBox.TextProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnDataErrors = true });
        layout.Children.Add(input);
        var suffix = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        suffix.SetBinding(TextBlock.TextProperty, new Binding(nameof(Unit)) { Source = this });
        SetColumn(suffix, 1); layout.Children.Add(suffix);
        var steps = new Grid();
        steps.RowDefinitions.Add(new RowDefinition());
        steps.RowDefinitions.Add(new RowDefinition());
        SetColumn(steps, 2); layout.Children.Add(steps);
        AddStep(steps, 1, 0); AddStep(steps, -1, 1);
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down) { Step(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            AutomationProperties.SetName(input, AutomationProperties.GetName(this));
            AutomationProperties.SetHelpText(input, RangeHelp);
            input.ToolTip = RangeHelp;
        };
    }

    private void AddStep(Grid steps, int direction, int row)
    {
        var arrow = new System.Windows.Shapes.Path { Data = Geometry.Parse(direction > 0 ? "M 0,4 L 4,0 L 8,4 Z" : "M 0,0 L 8,0 L 4,4 Z"), Fill = Brushes.LightGray, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = arrow, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0), BorderThickness = new Thickness(0) };
        button.Click += (_, _) => Step(direction);
        button.Loaded += (_, _) => AutomationProperties.SetName(button, $"{(direction > 0 ? "Increase" : "Decrease")} {AutomationProperties.GetName(this)}");
        SetRow(button, row); steps.Children.Add(button);
    }

    private void Step(int direction)
    {
        if (!int.TryParse(Value, out int number) || number < Minimum || number > Maximum) return;
        SetCurrentValue(ValueProperty, Math.Clamp(number + direction, Minimum, Maximum).ToString(CultureInfo.InvariantCulture));
    }
}
