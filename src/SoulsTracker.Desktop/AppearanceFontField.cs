using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Binding = System.Windows.Data.Binding;
using Brushes = System.Windows.Media.Brushes;

namespace SoulsTracker.Desktop;

/// <summary>Search is transient; only explicit selection changes the chosen font.</summary>
public sealed class AppearanceFontField : Grid
{
    public static readonly DependencyProperty FontsProperty = DependencyProperty.Register(nameof(Fonts), typeof(IEnumerable<string>), typeof(AppearanceFontField), new PropertyMetadata(null));
    public static readonly DependencyProperty SelectedFontProperty = DependencyProperty.Register(nameof(SelectedFont), typeof(string), typeof(AppearanceFontField), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public IEnumerable<string>? Fonts { get => (IEnumerable<string>?)GetValue(FontsProperty); set => SetValue(FontsProperty, value); }
    public string SelectedFont { get => (string)GetValue(SelectedFontProperty); set => SetValue(SelectedFontProperty, value); }
    private readonly Button choice;
    private readonly Popup popup;
    private readonly TextBox search;
    private readonly ListBox list;

    public AppearanceFontField()
    {
        MinWidth = 150; Height = 32;
        choice = new Button { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3), ToolTip = "Choose font. Type to filter the installed fonts." };
        choice.SetBinding(ContentControl.ContentProperty, new Binding(nameof(SelectedFont)) { Source = this });
        var caption = new FrameworkElementFactory(typeof(StackPanel));
        caption.SetValue(StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        var fontName = new FrameworkElementFactory(typeof(TextBlock));
        fontName.SetBinding(TextBlock.TextProperty, new Binding());
        caption.AppendChild(fontName);
        var arrow = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        arrow.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,0 L 8,0 L 4,4 Z"));
        arrow.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.LightGray);
        arrow.SetValue(MarginProperty, new Thickness(10, 0, 0, 0));
        arrow.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        caption.AppendChild(arrow);
        choice.ContentTemplate = new DataTemplate { VisualTree = caption };
        AutomationProperties.SetName(choice, "Choose overlay font");
        AutomationProperties.SetHelpText(choice, "Open to search installed fonts. Enter selects; Escape cancels.");
        Children.Add(choice);
        search = new TextBox { Margin = new Thickness(4), MinWidth = 0 };
        AutomationProperties.SetName(search, "Search installed fonts");
        list = new ListBox { Height = 200, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 36, 43)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(list, "Installed fonts");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        list.ItemContainerStyle = itemStyle;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Search fonts", Margin = new Thickness(8, 6, 8, 0) });
        panel.Children.Add(search); panel.Children.Add(list);
        var surface = new Border { Child = panel, Background = list.Background, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        popup = new Popup { PlacementTarget = choice, Placement = PlacementMode.Bottom, StaysOpen = false, Child = surface };
        Children.Add(popup);
        choice.Click += (_, _) =>
        {
            if (popup.IsOpen) { popup.IsOpen = false; return; }
            surface.Width = Math.Max(ActualWidth, 230);
            search.Text = ""; Filter(); popup.IsOpen = true;
            search.Focus();
        };
        search.TextChanged += (_, _) => Filter();
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { popup.IsOpen = false; choice.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down && search.IsKeyboardFocused && list.Items.Count > 0)
            {
                list.SelectedIndex = 0; list.UpdateLayout();
                ((ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(0))?.Focus(); e.Handled = true;
            }
        };
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem item)
            { list.SelectedItem = item.Content; Commit(); e.Handled = true; }
        };
        Unloaded += (_, _) => popup.IsOpen = false;
    }

    private void Filter() => list.ItemsSource = (Fonts ?? []).Where(font => font.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    private void Commit()
    {
        if (list.SelectedItem is not string font) return;
        SetCurrentValue(SelectedFontProperty, font);
        popup.IsOpen = false; choice.Focus();
    }
}
