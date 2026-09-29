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
    [Fact]
    public Task RepresentativeControlsKeepKeyboardFocusCuesButNotPointerCues() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow((_, _) => null, _ => { })
        {
            ShowInTaskbar = false,
            DataContext = new
            {
                ControlsEnabled = true,
                IsEldenRingSelected = true,
                IsEldenRingBrowseVisible = false,
                IsEldenRingChangeVisible = true,
                EldenRingDirectoryPath = "C:\\synthetic-save",
                PresentationControlsEnabled = true,
                HostedOverlay = new { CanCopy = true, UrlText = "https://overlay.beingkairo.com/soulstracker/#synthetic" }
            }
        };
        try
        {
            window.Show(); await Idle();
            var main = (TabItem)window.FindName("MainWorkspaceTab");
            var overlay = (TabItem)window.FindName("OverlayWorkspaceTab");
            var representatives = new (TabItem Tab, Control Control)[]
            {
                (overlay, (Button)window.FindName("ApplyAppearanceButton")),
                (main, DirectoryPresentationControlTests.Tree(window).OfType<Button>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Change Elden Ring save directory")),
                (main, DirectoryPresentationControlTests.Tree(window).OfType<Button>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Refresh Elden Ring saves")),
                (main, DirectoryPresentationControlTests.Tree(window).OfType<Button>().Single(x => x.IsVisible && System.Windows.Automation.AutomationProperties.GetName(x) == "Copy directory path")),
                (overlay, (Button)window.FindName("CopyTotalDeathsOverlayUrlButton"))
            };

            foreach (var (tab, control) in representatives)
            {
                tab.IsSelected = true; control.BringIntoView(); await Idle();
                var restingBorder = control is Button restingButton
                    ? ((Border)restingButton.Template.FindName("ButtonBorder", restingButton)).BorderBrush
                    : null;
                Keyboard.ClearFocus();
                SetInputModality(Mouse.PrimaryDevice); Hover(control);
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await Idle();
                Assert.True(control.IsKeyboardFocusWithin, System.Windows.Automation.AutomationProperties.GetName(control));
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(control);
                Assert.NotNull(layer);
                Assert.DoesNotContain(layer.GetAdorners(control) ?? [], adorner => adorner.IsVisible);
                if (control is Button button)
                    Assert.Equal(restingBorder, ((Border)button.Template.FindName("ButtonBorder", button)).BorderBrush);

                Keyboard.ClearFocus();
                SetInputModality(Keyboard.PrimaryDevice);
                Assert.True(control.Focus()); await Idle();
                Assert.Contains(layer.GetAdorners(control) ?? [], adorner => adorner.IsVisible);
            }

        }
        finally { Hover(null); Clipboard.Clear(); window.Close(); }
    });

    [Fact]
    public Task PointerActivationSelectsEachWorkspaceTabWithoutLeavingAFocusCue() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var tabs = (TabControl)window.FindName("WorkspaceTabs");
            var main = (TabItem)window.FindName("MainWorkspaceTab");
            var overlay = (TabItem)window.FindName("OverlayWorkspaceTab");
            var settings = (TabItem)window.FindName("SettingsWorkspaceTab");

            foreach (var (prior, target) in new[] { (main, overlay), (overlay, settings), (settings, main) })
            {
                prior.IsSelected = true; await Idle();
                Assert.False(target.IsSelected);
                var surface = (Border)target.Template.FindName("TabSurface", target);
                var restingBackground = surface.Background;

                Keyboard.ClearFocus();
                SetInputModality(Mouse.PrimaryDevice); Hover(target); await Idle();
                Assert.NotEqual(restingBackground, surface.Background);
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Assert.True(target.IsSelected);
                Assert.Same(target, tabs.SelectedItem);
                Assert.Equal(window.FindResource("SelectionBrush"), surface.Background);
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await Idle();

                Assert.True(target.IsSelected);
                Assert.Equal(window.FindResource("SelectionBrush"), surface.Background);
                Assert.Equal(window.FindResource("AccentBrush"), surface.BorderBrush);
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(target);
                Assert.NotNull(layer);
                Assert.DoesNotContain(layer.GetAdorners(target) ?? [], adorner => adorner.IsVisible);
            }
        }
        finally { Hover(null); window.Close(); }
    });

    [Fact]
    public Task WorkspaceKeyboardInputMovesThroughTabsAndRepresentativeControlsWithFocusCues() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow((_, _) => null, _ => { })
        {
            ShowInTaskbar = false,
            DataContext = new
            {
                ControlsEnabled = true,
                IsEldenRingSelected = true,
                IsEldenRingBrowseVisible = false,
                IsEldenRingChangeVisible = true,
                EldenRingDirectoryPath = "C:\\synthetic-save",
                PresentationControlsEnabled = true,
                HostedOverlay = new { CanCopy = true, UrlText = "https://overlay.beingkairo.com/soulstracker/#synthetic" }
            }
        };
        try
        {
            window.Show(); await Idle();
            var main = (TabItem)window.FindName("MainWorkspaceTab");
            var overlay = (TabItem)window.FindName("OverlayWorkspaceTab");
            var settings = (TabItem)window.FindName("SettingsWorkspaceTab");

            FocusByKeyboard(main); await Idle();
            SendKey(main, window, Key.Right); await Idle();
            AssertKeyboardFocus(overlay);
            overlay.IsSelected = true; await Idle(); FocusByKeyboard(overlay); await Idle();
            SendKey(overlay, window, Key.Left); await Idle();
            AssertKeyboardFocus(main);
            overlay.IsSelected = true; await Idle(); FocusByKeyboard(overlay); await Idle();
            SendKey(overlay, window, Key.Right); await Idle();
            AssertKeyboardFocus(settings);

            main.IsSelected = true; await Idle();
            var mainButtons = DirectoryPresentationControlTests.Tree(window).OfType<Button>().ToArray();
            var directoryCopy = mainButtons.Single(x => x.IsVisible && System.Windows.Automation.AutomationProperties.GetName(x) == "Copy directory path");
            var change = mainButtons.Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Change Elden Ring save directory");
            var refresh = mainButtons.Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Refresh Elden Ring saves");
            FocusByKeyboard(directoryCopy); await Idle();
            AssertKeyboardFocus(directoryCopy);
            SendKey(directoryCopy, window, Key.Tab); await Idle();
            AssertKeyboardFocus(change);
            SendKey(change, window, Key.Tab); await Idle();
            AssertKeyboardFocus(refresh);
            SendKey(refresh, window, Key.Tab, ModifierKeys.Shift); await Idle();
            AssertKeyboardFocus(change);

            overlay.IsSelected = true; await Idle();
            var url = DirectoryPresentationControlTests.Tree(window).OfType<TextBox>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Overlay URL");
            var copyUrl = (Button)window.FindName("CopyTotalDeathsOverlayUrlButton");
            FocusByKeyboard(url); await Idle();
            SendKey(url, window, Key.Tab); await Idle();
            AssertKeyboardFocus(copyUrl);

            var apply = (Button)window.FindName("ApplyAppearanceButton");
            var reset = (Button)window.FindName("ResetSelectedOverlayAppearanceButton");
            FocusByKeyboard(apply); await Idle();
            SendKey(apply, window, Key.Tab); await Idle();
            AssertKeyboardFocus(reset);
            SendKey(reset, window, Key.Tab, ModifierKeys.Shift); await Idle();
            AssertKeyboardFocus(apply);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SharedButtonKeepsEnterAndSpaceKeyboardActivation() => HostedConnectionTests.OnDispatcher(async () =>
    {
        int clicks = 0;
        var window = new MainWindow { ShowInTaskbar = false };
        var button = new Button { Content = "Activate" };
        button.Click += (_, _) => clicks++;
        window.Content = button;
        try
        {
            window.Show(); await Idle();
            SetInputModality(Keyboard.PrimaryDevice); Assert.True(button.Focus());
            var keyboard = new PressedKeyboard(Key.Enter);
            button.RaiseEvent(new KeyEventArgs(keyboard, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.Equal(1, clicks);

            keyboard.Pressed = Key.Space;
            button.RaiseEvent(new KeyEventArgs(keyboard, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space)
            { RoutedEvent = Keyboard.KeyDownEvent });
            Assert.True(button.IsPressed);
            keyboard.Pressed = Key.None;
            button.RaiseEvent(new KeyEventArgs(keyboard, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space)
            { RoutedEvent = Keyboard.KeyUpEvent });
            Assert.Equal(2, clicks);
            Assert.False(button.IsPressed);
        }
        finally { window.Close(); }
    });

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
            "number" => DirectoryPresentationControlTests.Tree(new AppearanceNumberField { Minimum = 12, Maximum = 96, Value = "24" }).OfType<TextBox>().Single(),
            "outline" => DirectoryPresentationControlTests.Tree(new AppearanceNumberField { Minimum = 0, Maximum = 8, Value = "3" }).OfType<TextBox>().Single(),
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
                if (family is "button" or "copy" or "help" or "tab" or "slider" or "color")
                {
                    var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(control);
                    Assert.NotNull(layer);
                    Assert.Contains(layer.GetAdorners(control) ?? [], adorner => adorner.IsVisible);
                }
            }
            Assert.Equal(baseline, Geometry(control));
            Assert.Equal(panelSize, panel.DesiredSize);
            if (control.Focusable)
            {
                neutral.Focus(); await Idle();
                // Drive the loaded control through WPF's pointer down/up route.
                SetInputModality(Mouse.PrimaryDevice);
                Hover(control);
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                if (family is "button" or "copy" or "help" or "tab") Assert.True(control.IsKeyboardFocusWithin);
                if (!control.IsKeyboardFocusWithin) Assert.True(control.Focus());
                control.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                await Idle();
                Assert.True(control.IsKeyboardFocusWithin);
                Assert.IsAssignableFrom<MouseDevice>(InputManager.Current.MostRecentInputDevice);
                if (family is "button" or "copy" or "help")
                {
                    var border = (Border)control.Template.FindName(family == "help" ? "HelpCircle" : "ButtonBorder", control);
                    Assert.NotEqual(window.FindResource("AccentBrush"), border.BorderBrush);
                }
                if (family is "button" or "copy" or "help" or "tab" or "slider" or "color")
                {
                    var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(control);
                    Assert.NotNull(layer);
                    Assert.DoesNotContain(layer.GetAdorners(control) ?? [], adorner => adorner.IsVisible);
                }
                Assert.Equal(baseline, Geometry(control));
            }
            if (control is ButtonBase button)
            {
                // Execute WPF's own Space-key state transition, then disable
                // before release so no application action or native dialog runs.
                button.RaiseEvent(new KeyEventArgs(new PressedKeyboard(), PresentationSource.FromVisual(window), Environment.TickCount, Key.Space)
                { RoutedEvent = Keyboard.KeyDownEvent });
                Assert.True(button.IsPressed);
                if (family is "button" or "copy")
                    Assert.Equal(window.FindResource("SelectionBrush"), ((Border)control.Template.FindName("ButtonBorder", control)).Background);
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

    private static void FocusByKeyboard(Control control)
    {
        SetInputModality(Keyboard.PrimaryDevice);
        Assert.True(control.Focus());
    }

    private static void SendKey(UIElement target, Window window, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        Assert.Same(target, Keyboard.FocusedElement);
        KeyboardDevice keyboard = modifiers == ModifierKeys.None
            ? Keyboard.PrimaryDevice
            : new PressedKeyboard(key, modifiers);
        if (!ReferenceEquals(keyboard, Keyboard.PrimaryDevice))
            keyboard.Focus(target);
        InputManager.Current.ProcessInput(new KeyEventArgs(keyboard, PresentationSource.FromVisual(window), Environment.TickCount, key)
        { RoutedEvent = Keyboard.KeyDownEvent });
        InputManager.Current.ProcessInput(new KeyEventArgs(keyboard, PresentationSource.FromVisual(window), Environment.TickCount, key)
        { RoutedEvent = Keyboard.KeyUpEvent });
    }

    private static void AssertKeyboardFocus(Control control)
    {
        Assert.True(control.IsKeyboardFocused, System.Windows.Automation.AutomationProperties.GetName(control));
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(control);
        Assert.NotNull(layer);
        Assert.Contains(layer.GetAdorners(control) ?? [], adorner => adorner.IsVisible);
    }

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

    private sealed class PressedKeyboard(Key pressed = Key.Space, ModifierKeys modifiers = ModifierKeys.None) : KeyboardDevice(InputManager.Current)
    {
        internal Key Pressed { get; set; } = pressed;
        protected override KeyStates GetKeyStatesFromSystem(Key key) =>
            key == Pressed ||
            key == Key.LeftShift && modifiers.HasFlag(ModifierKeys.Shift) ||
            key == Key.LeftCtrl && modifiers.HasFlag(ModifierKeys.Control) ||
            key == Key.LeftAlt && modifiers.HasFlag(ModifierKeys.Alt)
                ? KeyStates.Down
                : KeyStates.None;
    }


}
