using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.DirectoryPresentationControlTests;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class AppearanceFontTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LeavingFontEditorCancelsDistinctQueryWithoutMovingDestinationFocus(bool backwards) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        var publisher = new CountingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var initialState = repository.State;
        int publications = publisher.Count;
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate();
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var control = Tree(window).OfType<AppearanceFontField>().Single();
            control.BringIntoView(); await Idle();
            var search = Tree(control).OfType<TextBox>().Single();
            var popup = Tree(control).OfType<Popup>().Single();
            string selected = vm.TotalDeathsAppearanceDraft.FontFamily;
            string query = vm.LocalFontFamilies.First(font => font != selected);
            Assert.NotEqual(selected, query);
            Assert.True(search.Focus()); search.SelectAll();
            search.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, search, query)) { RoutedEvent = TextCompositionManager.TextInputEvent });
            await Idle();
            Assert.Equal(query, search.Text);
            Assert.True(popup.IsOpen);
            Assert.Equal(selected, control.SelectedFont);
            var direction = backwards ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
            Assert.True(search.MoveFocus(new TraversalRequest(direction))); await Idle();
            if (backwards)
            {
                Assert.Same(Tree(control).OfType<Button>().Single(), Keyboard.FocusedElement);
                Assert.True(popup.IsOpen);
                Assert.Equal(query, search.Text);
                Assert.True(((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(direction))); await Idle();
            }
            var destination = Assert.IsType<TextBox>(Keyboard.FocusedElement);
            Assert.Equal(backwards ? "Overlay title" : "Font size", System.Windows.Automation.AutomationProperties.GetName(destination));
            AppearanceGeometryTests.Capture(window, backwards ? "font-shift-tab" : "font-tab");
            Assert.False(popup.IsOpen);
            Assert.Equal(selected, search.Text);
            Assert.Equal(selected, vm.TotalDeathsAppearanceDraft.FontFamily);
            Assert.Same(destination, Keyboard.FocusedElement);
            Assert.Equal(initialState, repository.State);
            Assert.Empty(repository.Saves);
            Assert.Equal(publications, publisher.Count);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FontBoundaryRetainsNavigationExplicitSelectionAndUnavailableFonts(bool unavailable) => HostedConnectionTests.OnDispatcher(async () =>
    {
        string selected = unavailable ? "Unavailable Synthetic Font" : "Arial";
        var appearance = new OverlayAppearance("Total Deaths", selected, 24, "#FFFFFF", "#FFFFFF", "#000000", 0, 0, 0, OverlayTextAlignment.Left);
        var repository = new MemoryRepository(GameId.DemonsSouls)
        {
            State = new PersistentTrackerState(1, GameId.DemonsSouls,
                new OverlayConfiguration(1, new TotalDeathsOverlayOptions(true, false, true, appearance, OverlayTitleIconMode.Off)), eldenRingNoticeAcknowledged: true)
        };
        var publisher = new CountingPublisher();
        await using var coordinator = new SerializedTrackerCoordinator(repository, publisher);
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        // Loading retains the stored name but resolves the draft to an installed fallback.
        // Supply the unavailable name to the bound editor to test its own retention boundary.
        if (unavailable)
        {
            Assert.Equal("Segoe UI", vm.TotalDeathsAppearanceDraft.FontFamily);
            vm.TotalDeathsAppearanceDraft.FontFamily = selected;
        }
        var initialState = repository.State;
        int publications = publisher.Count;
        var window = new MainWindow { DataContext = vm, ShowInTaskbar = false };
        try
        {
            window.Show(); window.Activate();
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true; await Idle();
            var control = Tree(window).OfType<AppearanceFontField>().Single();
            control.BringIntoView(); await Idle();
            var search = Tree(control).OfType<TextBox>().Single();
            var choice = Tree(control).OfType<Button>().Single();
            var popup = Tree(control).OfType<Popup>().Single();
            var list = Tree(popup.Child).OfType<ListBox>().Single();
            var destination = Tree(window).OfType<TextBox>().Single(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Font size");
            Assert.Equal(selected, search.Text);
            if (unavailable) Assert.DoesNotContain(selected, vm.LocalFontFamilies);
            Assert.True(search.Focus());
            TypeQuery(search, "No Matching Synthetic Query"); await Idle();
            Assert.True(popup.IsOpen);
            Assert.Empty(list.Items);
            SendKey(search, window, Key.Enter, true); await Idle();
            Assert.Equal(selected, control.SelectedFont);
            Assert.True(popup.IsOpen);
            SendKey(search, window, Key.Escape, true); await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal(selected, search.Text);
            Assert.True(search.IsKeyboardFocused);

            TypeQuery(search, "Another Unmatched Query"); await Idle();
            // Model pointer-directed focus without changing the OS pointer or button state.
            destination.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent }); await Idle();
            Assert.True(destination.Focus()); await Idle();
            Assert.True(destination.IsKeyboardFocused);
            Assert.False(popup.IsOpen);
            Assert.Equal(selected, search.Text);
            Assert.Equal(selected, vm.TotalDeathsAppearanceDraft.FontFamily);

            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.True(popup.IsOpen);
            Assert.Equal(selected, search.Text);
            Assert.Equal(vm.LocalFontFamilies.Count, list.Items.Count);
            SendKey(search, window, Key.Down, true); await Idle();
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.True(first.IsKeyboardFocused);
            Assert.True(popup.IsOpen);
            SendKey(first, window, Key.Down); await Idle();
            Assert.Equal(1, list.SelectedIndex);
            SendKey((UIElement)Keyboard.FocusedElement, window, Key.Up); await Idle();
            Assert.Equal(0, list.SelectedIndex);
            Assert.Equal(selected, control.SelectedFont);
            Assert.True(choice.Focus()); await Idle();
            Assert.True(popup.IsOpen);
            Assert.True(search.Focus()); await Idle();
            Assert.True(popup.IsOpen);

            string query = vm.LocalFontFamilies.First(font => font != selected);
            TypeQuery(search, query); await Idle();
            Assert.NotEqual(selected, query);
            Assert.True(popup.IsOpen);
            SendKey(search, window, Key.Down, true); await Idle();
            string chosen = Assert.IsType<string>(list.SelectedItem);
            SendKey((UIElement)Keyboard.FocusedElement, window, Key.Enter); await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal(chosen, vm.TotalDeathsAppearanceDraft.FontFamily);
            Assert.Equal(chosen, search.Text);
            Assert.True(search.IsKeyboardFocused);

            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            string clicked = Assert.IsType<string>(item.Content);
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = item }); await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal(clicked, vm.TotalDeathsAppearanceDraft.FontFamily);
            Assert.Equal(clicked, search.Text);

            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            SendKey(search, window, Key.Down, true); await Idle();
            Assert.True(list.IsKeyboardFocusWithin);
            Assert.True(destination.Focus()); await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal(clicked, search.Text);
            Assert.True(destination.IsKeyboardFocused);
            Assert.True(search.Focus());
            string unique = vm.LocalFontFamilies.First(font => vm.LocalFontFamilies.Count(other => other.Contains(font, StringComparison.OrdinalIgnoreCase)) == 1);
            TypeQuery(search, unique); await Idle();
            Assert.Single(list.Items);
            SendKey(search, window, Key.Enter, true); await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal(unique, vm.TotalDeathsAppearanceDraft.FontFamily);
            Assert.Equal(unique, search.Text);
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.True(popup.IsOpen);
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal(unique, search.Text);
            Assert.Equal(initialState, repository.State);
            Assert.Empty(repository.Saves);
            Assert.Equal(publications, publisher.Count);
        }
        finally { window.Close(); }
    });

    private static void TypeQuery(TextBox search, string text)
    {
        search.SelectAll();
        search.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, search, text)) { RoutedEvent = TextCompositionManager.TextInputEvent });
    }

    private static void SendKey(UIElement target, Window window, Key key, bool preview = false) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent });

    [Fact]
    public Task FontSelectionFiltersWithoutCommittingSearchOrHover() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var window = new MainWindow { ShowInTaskbar = false };
        var type = typeof(MainWindow).Assembly.GetType("SoulsTracker.Desktop.AppearanceFontField");
        Assert.NotNull(type);
        var control = (FrameworkElement)Activator.CreateInstance(type)!;
        var fonts = Enumerable.Range(0, 80).Select(i => $"Font {i:D2}").ToArray();
        type.GetProperty("Fonts")!.SetValue(control, fonts);
        type.GetProperty("SelectedFont")!.SetValue(control, "Font 40");
        window.Content = control;
        try
        {
            window.Show(); await Idle();
            var popup = Tree(control).OfType<Popup>().Single();
            Assert.False(popup.IsOpen);
            var search = Assert.Single(Tree(control).OfType<TextBox>());
            Assert.Equal(TextAlignment.Left, search.TextAlignment);
            Assert.Equal(HorizontalAlignment.Left, search.HorizontalContentAlignment);
            search.Focus();
            search.Text = "fOnT 2"; await Idle();
            Assert.True(popup.IsOpen);
            Assert.Empty(Tree(popup.Child).OfType<TextBox>());
            var list = Tree(popup.Child).OfType<ListBox>().Single();
            Assert.Equal(10, list.Items.Count);
            AppearanceGeometryTests.Capture((FrameworkElement)popup.Child, "font-filtered");
            Assert.Equal("Font 40", type.GetProperty("SelectedFont")!.GetValue(control));
            search.Text = ""; await Idle();
            var scroll = Tree(list).OfType<ScrollViewer>().Single();
            scroll.ScrollToTop(); await Idle();
            double offset = scroll.VerticalOffset;
            var last = Tree(list).OfType<ListBoxItem>().Last();
            for (int sample = 0; sample < 16; sample++)
            {
                typeof(MouseDevice).GetMethod("ChangeMouseOver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(Mouse.PrimaryDevice, [last, Environment.TickCount]);
                Assert.True(last.IsMouseOver);
                await Task.Delay(50); await Idle();
                Assert.Equal(offset, scroll.VerticalOffset);
            }
            Assert.Equal(offset, scroll.VerticalOffset);
            ScrollBar.LineDownCommand.Execute(null, Tree(list).OfType<ScrollBar>().Single(x => x.Orientation == Orientation.Vertical)); await Idle();
            Assert.True(scroll.VerticalOffset > offset);
            offset = scroll.VerticalOffset;
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent }); await Idle();
            Assert.True(scroll.VerticalOffset > offset);
            list.Focus();
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.End) { RoutedEvent = Keyboard.KeyDownEvent }); await Idle();
            Assert.Equal("Font 79", list.SelectedItem);
            Assert.True(scroll.VerticalOffset > offset);
            Assert.Equal("Font 40", type.GetProperty("SelectedFont")!.GetValue(control));
            search.Text = "Font 25"; await Idle();
            list.SelectedIndex = 0;
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal("Font 25", type.GetProperty("SelectedFont")!.GetValue(control));
            Tree(control).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Assert.Equal("Font 25", search.Text);
            Assert.Equal(fonts.Length, list.Items.Count);
            search.Text = "no matches"; await Idle();
            search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Idle();
            Assert.False(popup.IsOpen);
            Assert.Equal("Font 25", type.GetProperty("SelectedFont")!.GetValue(control));
            Assert.Equal("Font 25", search.Text);
        }
        finally { window.Close(); }
    });
    private sealed class CountingPublisher : ITrackerStateChangePublisher
    {
        public int Count { get; private set; }
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
