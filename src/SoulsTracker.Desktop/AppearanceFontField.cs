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
    public static readonly DependencyProperty SelectedFontProperty = DependencyProperty.Register(nameof(SelectedFont), typeof(string), typeof(AppearanceFontField), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((AppearanceFontField)d).RestoreSelection()));
    public IEnumerable<string>? Fonts { get => (IEnumerable<string>?)GetValue(FontsProperty); set => SetValue(FontsProperty, value); }
    public string SelectedFont { get => (string)GetValue(SelectedFontProperty); set => SetValue(SelectedFontProperty, value); }
    private readonly Button choice;
    private readonly Popup popup;
    private readonly TextBox search;
    private readonly ListBox list;
    private bool restoring;

    public AppearanceFontField()
    {
        MinWidth = 150; Height = 32;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        choice = new Button { MinWidth = 0, Padding = new Thickness(0), ToolTip = "Show installed fonts", Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 8,0 L 4,4 Z"), Fill = Brushes.LightGray } };
        AutomationProperties.SetName(choice, "Choose overlay font");
        AutomationProperties.SetHelpText(choice, "Open to search installed fonts. Enter selects; Escape cancels.");
        SetColumn(choice, 1); Children.Add(choice);
        search = new TextBox { MinWidth = 0, TextAlignment = TextAlignment.Left, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(search, "Search installed fonts");
        AutomationProperties.SetHelpText(search, "Type to filter installed fonts. Down opens results; Enter selects; Escape restores the chosen font.");
        Children.Add(search);
        list = new ListBox { Height = 200, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 36, 43)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(list, "Installed fonts");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        list.ItemContainerStyle = itemStyle;
        var panel = new StackPanel();
        panel.Children.Add(list);
        var surface = new Border { Child = panel, Background = list.Background, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        popup = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, Child = surface };
        Children.Add(popup);
        choice.Click += (_, _) =>
        {
            if (popup.IsOpen) { popup.IsOpen = false; return; }
            list.ItemsSource = (Fonts ?? []).ToArray(); Open();
            search.Focus();
        };
        search.TextChanged += (_, _) => { if (!restoring) { Filter(); Open(); } };
        search.PreviewKeyDown += HandleKey;
        panel.PreviewKeyDown += HandleKey;
        LostKeyboardFocus += HandleFocusDeparture;
        surface.LostKeyboardFocus += HandleFocusDeparture;
        popup.Closed += (_, _) => RestoreSelection();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem item)
            { list.SelectedItem = item.Content; Commit(); e.Handled = true; }
        };
        Unloaded += (_, _) => popup.IsOpen = false;
    }

    private void HandleFocusDeparture(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Popup focus crosses a separate presentation source; check after the transfer settles.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!popup.IsOpen || IsKeyboardFocusWithin || popup.Child.IsKeyboardFocusWithin) return;
            popup.IsOpen = false;
        }));
    }

    private void Filter() => list.ItemsSource = (Fonts ?? []).Where(font => font.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    private void Open()
    {
        ((FrameworkElement)popup.Child).Width = Math.Max(ActualWidth, 230);
        popup.IsOpen = IsLoaded;
    }
    private void RestoreSelection()
    {
        if (search is null) return;
        restoring = true;
        search.Text = SelectedFont;
        restoring = false;
    }
    private void HandleKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { popup.IsOpen = false; RestoreSelection(); search.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && search.IsKeyboardFocused)
        {
            if (list.SelectedItem is null && list.Items.Count == 1) list.SelectedIndex = 0;
            Commit(); e.Handled = true;
        }
        else if (e.Key == Key.Down && search.IsKeyboardFocused)
        {
            if (!popup.IsOpen) { list.ItemsSource = (Fonts ?? []).ToArray(); Open(); }
            if (list.Items.Count > 0)
            {
                list.SelectedIndex = 0; list.UpdateLayout();
                ((ListBoxItem?)list.ItemContainerGenerator.ContainerFromIndex(0))?.Focus();
            }
            e.Handled = true;
        }
    }
    private void Commit()
    {
        if (list.SelectedItem is not string font) return;
        SetCurrentValue(SelectedFontProperty, font);
        popup.IsOpen = false; RestoreSelection(); search.Focus();
    }
}
