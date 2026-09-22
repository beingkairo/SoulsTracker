using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class InteractiveStyleGeometryTests
{
    [Theory]
    [InlineData("button")]
    [InlineData("copy")]
    [InlineData("help")]
    [InlineData("text")]
    [InlineData("number")]
    [InlineData("outline")]
    [InlineData("appearance-dropdown")]
    [InlineData("color-text")]
    [InlineData("dropdown")]
    [InlineData("item")]
    [InlineData("tab")]
    [InlineData("check")]
    [InlineData("appearance-check")]
    [InlineData("slider")]
    [InlineData("color")]
    [InlineData("scroll-arrow")]
    [InlineData("scroll-thumb")]
    public Task LoadedStyleStatesKeepGeometry(string family) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow((_, _) => null, _ => { }) { ShowInTaskbar = false };
        Control control = family switch
        {
            "button" => new Button { Content = "Sample action" },
            "copy" => new Button { Style = (Style)window.FindResource("DirectoryCopyButton") },
            "help" => new Button { Style = (Style)window.FindResource("ContextHelpButton") },
            "text" => new TextBox { Text = "Sample text" },
            "number" => new AppearanceNumberField { Minimum = 12, Maximum = 96, Value = "24" }.Children.OfType<TextBox>().Single(),
            "outline" => new AppearanceNumberField { Minimum = 0, Maximum = 8, Value = "3" }.Children.OfType<TextBox>().Single(),
            "appearance-dropdown" => new ComboBox { ItemsSource = new List<string> { "Equal A", "Equal B" }, SelectedIndex = 0, Style = (Style)window.FindResource("OverlayAppearanceComboBox") },
            "color-text" => new ColorField().Children.OfType<TextBox>().Single(),
            "dropdown" => new ComboBox { ItemsSource = new List<string> { "Equal A", "Equal B" }, SelectedIndex = 0 },
            "item" => new ComboBoxItem { Content = "Equal item", Style = (Style)window.FindResource("DarkComboBoxItem") },
            "tab" => new TabItem { Header = "Sample", Style = (Style)window.FindResource("DarkTabItem") },
            "check" => new CheckBox { Content = "Sample option" },
            "appearance-check" => new CheckBox { Content = "Sample option", Style = (Style)window.FindResource("OverlayAppearanceCheckBox") },
            "slider" => new Slider { Width = 200, Style = (Style)window.FindResource("DarkSlider") },
            "color" => new ColorField().Children.OfType<Button>().Single(),
            "scroll-arrow" => new RepeatButton { Content = "v", Style = (Style)window.FindResource("ScrollBarArrowButton") },
            "scroll-thumb" => new Thumb { Width = 14, Style = (Style)window.FindResource("ScrollBarThumb") },
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        if (control.Parent is Panel original) original.Children.Remove(control);
        if (family == "color")
        {
            // Production has one UI dispatcher. Each test owns a fresh one;
            // create the identical focus style on this dispatcher's thread.
            control.FocusVisualStyle = (Style)typeof(ColorField).GetMethod("CreateKeyboardFocusVisualStyle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        }
        control.HorizontalAlignment = HorizontalAlignment.Left;
        var neutral = new Button { Content = "Neutral focus" };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(neutral); panel.Children.Add(control);
        window.Content = panel;
        try
        {
            window.Show(); await Idle();
            neutral.Focus(); Hover(null); await Idle();
            var baseline = Geometry(control);
            var panelSize = panel.DesiredSize;
            Hover(control); await Idle();
            Assert.Same(control, Mouse.DirectlyOver);
            Assert.True(control.IsMouseOver);
            Assert.Equal(baseline, Geometry(control));
            Hover(null); await Idle();
            Assert.False(control.IsMouseOver);
            if (control.Focusable)
            {
                SetInputModality(Keyboard.PrimaryDevice);
                Assert.True(control.Focus()); await Idle();
                Assert.True(control.IsKeyboardFocusWithin);
                Assert.IsAssignableFrom<KeyboardDevice>(InputManager.Current.MostRecentInputDevice);
                if (family == "color")
                {
                    var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(control);
                    Assert.NotNull(layer);
                    Assert.Contains(layer.GetAdorners(control) ?? [], adorner => adorner.IsVisible);
                }
                if (family is "button" or "copy" or "help")
                {
                    var border = (Border)control.Template.FindName(family == "help" ? "HelpCircle" : "ButtonBorder", control);
                    Assert.Equal(window.FindResource("AccentBrush"), border.BorderBrush);
                }
                if (family == "slider")
                    Assert.Equal(1, ((Border)control.Template.FindName("FocusRing", control)).Opacity);
            }
            Assert.Equal(baseline, Geometry(control));
            Assert.Equal(panelSize, panel.DesiredSize);
            if (control.Focusable)
            {
                neutral.Focus(); await Idle();
                // Establish the mouse input modality without OS cursor movement;
                // focus the loaded target through WPF and verify both states.
                SetInputModality(Mouse.PrimaryDevice);
                Assert.True(control.Focus());
                await Idle();
                Assert.True(control.IsKeyboardFocusWithin);
                Assert.IsAssignableFrom<MouseDevice>(InputManager.Current.MostRecentInputDevice);
                Assert.Equal(baseline, Geometry(control));
            }
            if (control is ButtonBase button)
            {
                // Execute WPF's own Space-key state transition, then disable
                // before release so no application action or native dialog runs.
                button.RaiseEvent(new KeyEventArgs(new PressedKeyboard(), PresentationSource.FromVisual(window), Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.True(button.IsPressed);
                window.UpdateLayout();
                Assert.Equal(baseline, Geometry(control));
                button.IsEnabled = false; await Idle();
                Assert.False(button.IsPressed);
                Assert.Equal(baseline, Geometry(control));
                button.IsEnabled = true;
            }
            if (control is ToggleButton toggle)
            {
                toggle.IsChecked = true; await Idle(); Assert.True(toggle.IsChecked);
                Assert.Equal(baseline, Geometry(control));
            }
            if (control is TabItem tab)
            {
                tab.IsSelected = true; await Idle(); Assert.True(tab.IsSelected);
                Assert.Equal(baseline, Geometry(control));
            }
            if (control is ComboBoxItem item)
            {
                item.IsSelected = true; await Idle(); Assert.True(item.IsSelected);
                Assert.Equal(baseline, Geometry(control));
            }
            if (control is ComboBox combo)
            {
                combo.IsDropDownOpen = true; await Idle(); Assert.True(combo.IsDropDownOpen);
                Assert.True(((ToggleButton)combo.Template.FindName("PART_ToggleButton", combo)).IsChecked);
                Assert.Equal(baseline, Geometry(control));
                combo.IsDropDownOpen = false;
            }
            if (control is Thumb || control is Slider)
            {
                var thumb = control as Thumb ?? (Thumb)((Slider)control).Template.FindName("SliderThumb", control);
                Hover(thumb); await Idle(); Assert.True(thumb.IsMouseOver);
                Assert.Equal(baseline, Geometry(control));
                thumb.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.MouseDownEvent });
                Assert.True(thumb.IsDragging);
                window.UpdateLayout(); Assert.Equal(baseline, Geometry(control));
                thumb.CancelDrag(); Hover(null); await Idle(); Assert.False(thumb.IsDragging);
            }
            neutral.Focus(); control.IsEnabled = false; await Idle();
            Assert.Equal(baseline, Geometry(control));
        }
        finally { Hover(null); window.Close(); }
    });

    private static void Hover(IInputElement? element)
    {
        // Drive the WPF mouse device transition, including reverse-inherited
        // IsMouseOver and routed enter/leave; this is synthetic, not OS input.
        var transition = typeof(MouseDevice).GetMethod("ChangeMouseOver", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(transition);
        transition.Invoke(Mouse.PrimaryDevice, [element, Environment.TickCount]);
    }

    private static void SetInputModality(InputDevice device) =>
        typeof(InputManager).GetProperty(nameof(InputManager.MostRecentInputDevice))!.SetValue(InputManager.Current, device);

    private static string[] Geometry(FrameworkElement root) => DirectoryPresentationControlTests.Tree(root)
        .OfType<FrameworkElement>().Where(element => element is not System.Windows.Documents.Adorner).Prepend(root).Select(element =>
        {
            string detail = element switch
            {
                Border border => $"{border.Padding}|{border.BorderThickness}",
                Control control => $"{control.Padding}|{control.BorderThickness}",
                _ => ""
            };
            return $"{element.GetType().Name}|{element.DesiredSize}|{element.RenderSize}|{element.Margin}|{detail}";
        }).ToArray();

    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    private sealed class PressedKeyboard() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key == Key.Space ? KeyStates.Down : KeyStates.None;
    }


}
