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
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        input = new TextBox { MinWidth = 0, Padding = new Thickness(5, 3, 4, 3), VerticalContentAlignment = VerticalAlignment.Center };
        input.SetBinding(TextBox.TextProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnDataErrors = true });
        Children.Add(input);
        var suffix = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        suffix.SetBinding(TextBlock.TextProperty, new Binding(nameof(Unit)) { Source = this });
        SetColumn(suffix, 1); Children.Add(suffix);
        AddStep("-", -1, 2); AddStep("+", 1, 3);
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

    private void AddStep(string label, int direction, int column)
    {
        var button = new Button { Content = label, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0) };
        button.Click += (_, _) => Step(direction);
        button.Loaded += (_, _) => AutomationProperties.SetName(button, $"{(direction > 0 ? "Increase" : "Decrease")} {AutomationProperties.GetName(this)}");
        SetColumn(button, column); Children.Add(button);
    }

    private void Step(int direction)
    {
        if (!int.TryParse(Value, out int number) || number < Minimum || number > Maximum) return;
        SetCurrentValue(ValueProperty, Math.Clamp(number + direction, Minimum, Maximum).ToString(CultureInfo.InvariantCulture));
    }
}
