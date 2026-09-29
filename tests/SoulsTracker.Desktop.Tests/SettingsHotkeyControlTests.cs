using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using static SoulsTracker.Desktop.Tests.SaveDirectoryWorkflowTests;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class SettingsHotkeyControlTests
{
    private static readonly string[] HotkeyFieldNames = ["IncrementHotkeyTextBox", "DecrementHotkeyTextBox"];

    [Theory]
    [InlineData(560d, 400d, false)]
    [InlineData(560d, 760d, false)]
    [InlineData(1060d, 760d, false)]
    [InlineData(560d, 400d, true)]
    [InlineData(560d, 760d, true)]
    [InlineData(1060d, 760d, true)]
    public Task ApplySuccessFloatsAndLatestActionOwnsExpiry(double width, double height, bool recorded) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        vm.ConfigureGlobalHotkeys(GlobalHotkeySettings.Default, _ => Task.FromResult(GlobalHotkeyRegistrationResult.Registered));
        vm.SetGlobalHotkeyStatus(GlobalHotkeyRegistrationResult.Registered.StatusMessage);
        var callbacks = new List<Action>();
        var window = new MainWindow((_, _) => null, _ => { }, (delay, callback) =>
        {
            Assert.Equal(TimeSpan.FromSeconds(5), delay);
            callbacks.Add(callback);
            return () => { };
        })
        { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
            var apply = (Button)window.FindName("ApplyHotkeysButton");
            var field = (TextBox)window.FindName("IncrementHotkeyTextBox");
            FrameworkElement anchor = recorded ? field : apply;
            anchor.BringIntoView(); await Idle();
            var stack = (StackPanel)window.FindName("SettingsContentStack");
            var before = stack.RenderSize;
            async Task Apply()
            {
                if (recorded)
                {
                    Assert.True(field.Focus()); await Idle();
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                }
                else apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Idle();
            }
            await Apply();
            var feedback = Assert.IsType<TextBlock>(window.FindName("HotkeySuccessStatus"));
            Assert.Equal("Hotkeys applied successfully", feedback.Text);
            Assert.True(feedback.IsVisible);
            var scroll = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            var viewport = DirectoryPresentationControlTests.Tree(scroll).OfType<ScrollContentPresenter>().First();
            var bounds = feedback.TransformToVisual(viewport).TransformBounds(new Rect(feedback.RenderSize));
            Assert.True(new Rect(viewport.RenderSize).Contains(bounds));
            Assert.False(((Border)window.FindName("HotkeySuccessOverlay")).IsHitTestVisible);
            RecordSettings(window, $"confirmation-{width}x{height}-{recorded}");
            AssertHotkeyTextReadable(window);
            Assert.Equal(before, stack.RenderSize);
            await Apply();
            Assert.Equal(2, callbacks.Count);
            callbacks[0](); await Idle();
            Assert.Equal("Hotkeys applied successfully", feedback.Text);
            callbacks[1](); await Idle();
            Assert.Empty(feedback.Text);
            Assert.Equal(before, stack.RenderSize);
        }
        finally { window.Close(); }
    });

    public static IEnumerable<object[]> ReadabilityCases()
    {
        foreach (var (width, height) in new[] { (560d, 400d), (560d, 760d), (1060d, 760d) })
            foreach (string action in new[] { "apply", "increment", "decrement" })
                foreach (bool longBinding in new[] { false, true })
                    yield return [width, height, action, longBinding];
    }

    [Theory]
    [MemberData(nameof(ReadabilityCases))]
    public Task ConfirmationKeepsBindingsReadableDuringViewportChanges(double width, double height, string action, bool longBinding) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var native = new Native();
        using var service = new DesktopGlobalHotkeyService(new Sink(), native, vm);
        Assert.True(service.Start().IsRegistered);
        vm.ConfigureGlobalHotkeys(service.ActiveSettings, async candidate =>
        {
            var result = service.Replace(candidate);
            if (result.IsRegistered)
                await coordinator.SetGlobalHotkeysAsync(new(candidate.Increment.Modifiers, candidate.Increment.VirtualKey,
                    candidate.Decrement.Modifiers, candidate.Decrement.VirtualKey));
            return result;
        });
        vm.SetGlobalHotkeyStatus(GlobalHotkeyRegistrationResult.Registered.StatusMessage);
        if (longBinding)
        {
            // Down Arrow is the longest supported key label. Exercise it in
            // the other field as well, with every permitted modifier.
            vm.CapturePendingHotkey(true, Key.Down, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
            vm.CapturePendingHotkey(false, Key.Up, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift);
        }
        // Isolate floating geometry from removal of the persistent edit prompt.
        vm.SetGlobalHotkeyStatus(GlobalHotkeyRegistrationResult.Registered.StatusMessage);
        var callbacks = new List<Action>();
        var window = new MainWindow((_, _) => null, _ => { }, (_, callback) => { callbacks.Add(callback); return () => { }; })
        { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            ((TabItem)window.FindName("SettingsWorkspaceTab")).IsSelected = true; await Idle();
            var apply = (Button)window.FindName("ApplyHotkeysButton");
            var increment = (TextBox)window.FindName("IncrementHotkeyTextBox");
            var decrement = (TextBox)window.FindName("DecrementHotkeyTextBox");
            var scroll = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            var viewport = DirectoryPresentationControlTests.Tree(scroll).OfType<ScrollContentPresenter>().First();
            var stack = (StackPanel)window.FindName("SettingsContentStack");
            var layer = (Canvas)window.FindName("FloatingFeedbackLayer");
            var overlay = (Border)window.FindName("HotkeySuccessOverlay");
            FrameworkElement anchor = action == "apply" ? apply : action == "increment" ? increment : decrement;
            anchor.BringIntoView(); await Idle();
            if (action == "apply") { Assert.True(apply.Focus()); await Idle(); }
            Rect Bounds(FrameworkElement element) => element.TransformToVisual(window).TransformBounds(new Rect(element.RenderSize));
            var before = (stack.RenderSize, scroll.VerticalOffset, scroll.ExtentHeight, Bounds(increment), Bounds(decrement), Bounds(apply));
            var expectedIncrement = vm.PendingIncrementHotkey;
            var expectedDecrement = vm.PendingDecrementHotkey;
            if (action == "apply") apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else
            {
                Assert.True(anchor.Focus()); await Idle(); Assert.True(vm.IsHotkeyRecording);
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            await Idle();
            await HostedDesktopPublisherTests.WaitUntil(() => callbacks.Count == 1);
            await Idle();
            string name = $"readability-{width}x{height}-{action}-{longBinding}";
            RecordSettings(window, name);
            Assert.Equal(before, (stack.RenderSize, scroll.VerticalOffset, scroll.ExtentHeight, Bounds(increment), Bounds(decrement), Bounds(apply)));
            Assert.Equal(new System.Windows.Size(), layer.DesiredSize);
            Assert.Equal("Hotkeys applied successfully", ((TextBlock)window.FindName("HotkeySuccessStatus")).Text);
            Assert.True(overlay.IsVisible);
            Assert.True(Bounds(viewport).Contains(Bounds(overlay)));
            AssertHotkeyTextReadable(window);
            Assert.Equal(expectedIncrement, vm.ActiveIncrementHotkey);
            Assert.Equal(expectedDecrement, vm.ActiveDecrementHotkey);
            Assert.Equal(service.ActiveSettings.Increment.VirtualKey, repository.State.GlobalHotkeys.IncrementVirtualKey);
            Assert.Equal(service.ActiveSettings.Decrement.VirtualKey, repository.State.GlobalHotkeys.DecrementVirtualKey);
            Assert.Single(repository.Saves);
            Assert.Equal(2, native.Registered.Count);
            Assert.Same(action == "apply" ? (IInputElement)apply : scroll, Keyboard.FocusedElement);
            Assert.False(overlay.IsHitTestVisible);
            var point = Bounds(overlay).TopLeft + new Vector(4, 4);
            Assert.DoesNotContain(overlay, Ancestors(window.InputHitTest(point) as DependencyObject));
            // Sweep the real viewport while feedback remains alive, including
            // transitions between the action row and heading row.
            double originalOffset = scroll.VerticalOffset;
            for (double offset = 0; offset <= scroll.ScrollableHeight; offset += 4)
            {
                scroll.ScrollToVerticalOffset(offset); await Idle();
                if (!overlay.IsVisible) continue;
                Assert.True(Bounds(viewport).Contains(Bounds(overlay)));
                AssertHotkeyTextReadable(window);
            }
            scroll.ScrollToVerticalOffset(originalOffset); await Idle();
            window.Width = width == 560 ? 1060 : 560; await Idle();
            if (overlay.IsVisible) { Assert.True(Bounds(viewport).Contains(Bounds(overlay))); AssertHotkeyTextReadable(window); }
            window.Width = width; await Idle();
            window.Height = height == 400 ? 760 : 400; await Idle();
            if (overlay.IsVisible) { Assert.True(Bounds(viewport).Contains(Bounds(overlay))); AssertHotkeyTextReadable(window); }
            window.Height = height; await Idle();
            callbacks.Single()(); await Idle();
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(before.Item1, stack.RenderSize);
            // The neighboring binding still accepts capture, and cancel returns
            // to a safe focus target without reviving the old confirmation.
            decrement.BringIntoView(); await Idle(); Assert.True(decrement.Focus()); await Idle();
            Assert.True(vm.IsHotkeyRecording);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Idle();
            Assert.Same(scroll, Keyboard.FocusedElement);
            Assert.False(vm.IsHotkeyRecording);
            Assert.Equal(expectedDecrement, vm.PendingDecrementHotkey);
            Assert.Empty(((TextBlock)window.FindName("HotkeySuccessStatus")).Text);
        }
        finally { window.Close(); }
    });

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element)) yield return element;
    }

    [Fact]
    public Task OlderCommittedApplyKeepsBindingsSyncedAfterNewerRegistrationFailure() => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var native = new Native();
        using var service = new DesktopGlobalHotkeyService(new Sink(), native, vm);
        Assert.True(service.Start().IsRegistered);
        var release = new TaskCompletionSource();
        vm.ConfigureGlobalHotkeys(service.ActiveSettings, async candidate =>
        {
            var result = service.Replace(candidate);
            if (!result.IsRegistered) return result;
            await release.Task;
            await coordinator.SetGlobalHotkeysAsync(new(candidate.Increment.Modifiers, candidate.Increment.VirtualKey,
                candidate.Decrement.Modifiers, candidate.Decrement.VirtualKey));
            return result;
        });
        vm.CapturePendingHotkey(true, Key.F7, ModifierKeys.Control);
        Task<bool> first = vm.ApplyGlobalHotkeysAsync();
        native.RejectedKey = 119;
        vm.CapturePendingHotkey(true, Key.F8, ModifierKeys.Control);
        Assert.False(await vm.ApplyGlobalHotkeysAsync());
        string? failure = vm.GlobalHotkeyStatus;
        release.SetResult();
        Assert.False(await first); // Its notification was superseded, not its commit.
        Assert.Equal(118u, service.ActiveSettings.Increment.VirtualKey);
        Assert.Equal(118u, repository.State.GlobalHotkeys.IncrementVirtualKey);
        Assert.Equal("Ctrl+F7", vm.ActiveIncrementHotkey);
        Assert.Equal("Ctrl+F7", vm.PendingIncrementHotkey);
        Assert.Equal(failure, vm.GlobalHotkeyStatus);
    });

    [Theory]
    [InlineData(560, 400)]
    [InlineData(560, 760)]
    [InlineData(1060, 760)]
    public Task SettingsEntryAndReturnKeepHelpClosedForEveryGame(int width, int height) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, Width = width, Height = height, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var settings = (TabItem)window.FindName("SettingsWorkspaceTab");
            var main = (TabItem)window.FindName("MainWorkspaceTab");
            var help = (Button)window.FindName("GlobalHotkeysHelpButton");
            var tooltip = (ToolTip)help.ToolTip;
            Assert.Equal(9, vm.GameChoices.Count);
            foreach (var choice in vm.GameChoices)
            {
                await vm.SelectGameAsync(choice); await Idle();
                settings.Focus(); settings.IsSelected = true; await Idle();
                Assert.False(tooltip.IsOpen);
                Assert.True(help.IsVisible);
                var field = (TextBox)window.FindName("IncrementHotkeyTextBox");
                Assert.True(field.IsVisible);
                Assert.Equal(vm.IsGlobalHotkeyConfigurationAvailable, field.IsEnabled);
                if (!vm.IsGlobalHotkeyConfigurationAvailable)
                    Assert.Contains("not available for this game", vm.GlobalHotkeyUsageDescription);
                Assert.DoesNotContain(DirectoryPresentationControlTests.Tree(window).OfType<TextBlock>(),
                    x => x.GetBindingExpression(TextBlock.TextProperty)?.ParentBinding.Path?.Path == nameof(vm.GlobalHotkeyUsageDescription));
                string expectedHelp = vm.GlobalHotkeyUsageDescription + " Choose a field to record. Enter saves; Esc cancels. Both return to the view where recording started.";
                Assert.Equal(expectedHelp, System.Windows.Automation.AutomationProperties.GetHelpText(help));
                Assert.Equal(vm.IsGlobalHotkeyConfigurationAvailable, ((TextBox)window.FindName("DecrementHotkeyTextBox")).IsEnabled);
                Assert.Equal(vm.IsGlobalHotkeyConfigurationAvailable, ((Button)window.FindName("ApplyHotkeysButton")).IsEnabled);
                var panels = ((StackPanel)window.FindName("SettingsContentStack")).Children.OfType<Border>().ToArray();
                Assert.Equal("Update check settings", System.Windows.Automation.AutomationProperties.GetName(panels[0]));
                Assert.Equal("Global hotkeys settings", System.Windows.Automation.AutomationProperties.GetName(panels[1]));
                RecordSettings(window, choice.GameId.Value);
                help.BringIntoView(); await Idle();
                var heading = Assert.IsType<StackPanel>(help.Parent);
                var panel = Assert.IsType<StackPanel>(heading.Parent);
                var incrementRow = (FrameworkElement)field.Parent;
                Assert.InRange(incrementRow.TranslatePoint(new Point(), panel).Y - (heading.TranslatePoint(new Point(), panel).Y + heading.ActualHeight), 7.5, 8.5);
                Assert.True(ToolTipService.GetIsEnabled(help));
                Assert.Equal(60000, ToolTipService.GetShowDuration(help));
                Assert.True(help.Focus()); await Idle();
                Assert.True(tooltip.IsOpen);
                Assert.Equal(expectedHelp, Assert.IsType<TextBlock>(tooltip.Content).Text);
                AppearanceGeometryTests.Capture(window, $"section-hotkeys-{choice.GameId.Value}-{width}x{height}");

                AppearanceGeometryTests.Capture(tooltip, $"tooltip-hotkeys-{choice.GameId.Value}-{width}x{height}");
                Assert.True(ToolTipService.GetIsEnabled(help));
                main.Focus(); main.IsSelected = true; await Idle();
                Assert.False(help.IsVisible);
                settings.Focus(); settings.IsSelected = true; await Idle();
                Assert.False(tooltip.IsOpen);
                SectionHelpControlTests.AssertPointerAvailable(help);
                main.Focus(); main.IsSelected = true; await Idle();
            }
            // Exercise WPF TabItem mouse-selection routing separately from
            // Focus(), then intentional keyboard traversal within Settings.
            settings.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseDownEvent });
            await Idle();
            Assert.True(settings.IsSelected);
            Assert.False(tooltip.IsOpen);
            var content = (ScrollViewer)window.FindName("SettingsContentScrollViewer");
            Assert.True(content.Focus()); await Idle();
            for (int i = 0; i < 8 && !help.IsKeyboardFocused; i++)
            {
                Assert.IsAssignableFrom<UIElement>(Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                await Idle();
            }
            Assert.True(help.IsKeyboardFocused);
            Assert.True(tooltip.IsOpen);
            content.Focus(); await Idle();
            Assert.False(tooltip.IsOpen);
            Assert.NotNull(help.ToolTip);
            Assert.True(ToolTipService.GetIsEnabled(help));
            Assert.False(vm.IsHotkeyRecording);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SettingsCaptureKeepsSaveCancelAndOriginReturnContract(bool save) => HostedConnectionTests.OnDispatcher(async () =>
    {
        var repository = new MemoryRepository(GameId.DemonsSouls);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var vm = CreateViewModel(coordinator); await vm.InitializeAsync();
        var native = new Native();
        using var service = new DesktopGlobalHotkeyService(new Sink(), native, vm);
        Assert.True(service.Start().IsRegistered);
        vm.ConfigureGlobalHotkeys(service.ActiveSettings, async candidate =>
        {
            var result = service.Replace(candidate);
            if (result.IsRegistered) await coordinator.SetGlobalHotkeysAsync(new(candidate.Increment.Modifiers, candidate.Increment.VirtualKey,
                candidate.Decrement.Modifiers, candidate.Decrement.VirtualKey));
            return result;
        });
        string original = vm.PendingIncrementHotkey;
        var window = new MainWindow((_, _) => null, _ => { }) { DataContext = vm, Width = 560, Height = 400, ShowInTaskbar = false };
        try
        {
            window.Show(); await Idle();
            var settings = (TabItem)window.FindName("SettingsWorkspaceTab");
            settings.IsSelected = true; await Idle();
            var field = (TextBox)window.FindName("IncrementHotkeyTextBox");
            field.BringIntoView(); await Idle();
            field.Focus(); await Idle();
            Assert.True(vm.IsHotkeyRecording);
            Assert.Same(window.FindName("HotkeyRecordingOverlay"), Keyboard.FocusedElement);
            vm.CaptureRecordedHotkey(Key.F7, ModifierKeys.Control);
            Assert.Equal("Ctrl+F7", vm.PendingIncrementHotkey);
            Assert.Empty(repository.Saves);
            // A tab transition does not discard the in-progress capture.
            ((TabItem)window.FindName("OverlayWorkspaceTab")).IsSelected = true;
            await Idle();
            Assert.True(vm.IsHotkeyRecording);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, save ? Key.Enter : Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(key);
            await HostedDesktopPublisherTests.WaitUntil(() => !vm.IsHotkeyRecording);
            await Idle();
            Assert.True(key.Handled);
            Assert.True(settings.IsSelected);
            Assert.Same(window.FindName("SettingsContentScrollViewer"), Keyboard.FocusedElement);
            var confirmation = (TextBlock)window.FindName("HotkeySuccessStatus");
            Assert.Equal(save ? "Hotkeys applied successfully" : string.Empty, confirmation.Text);
            Assert.Equal(save, confirmation.IsVisible);
            Assert.Equal(save ? "Ctrl+F7" : original, vm.PendingIncrementHotkey);
            Assert.Equal(save ? 1 : 0, repository.Saves.Count);
            Assert.Equal(save ? 118u : GlobalHotkeySettings.Default.Increment.VirtualKey, service.ActiveSettings.Increment.VirtualKey);
            Assert.Equal(2, native.Registered.Count);
            if (save) Assert.Equal(118u, repository.State.GlobalHotkeys.IncrementVirtualKey);
            Assert.True(await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.IncrementHotkeyId));
            Assert.Equal(1, vm.ManualDeaths);
            Assert.True(await service.HandleMessageAsync(GlobalHotkeyController.WindowsHotkeyMessage, GlobalHotkeyController.DecrementHotkeyId));
            Assert.Equal(0, vm.ManualDeaths);
            await vm.SelectGameAsync(vm.GameChoices.Single(x => x.GameId == GameId.Bloodborne));
            settings.IsSelected = true; await Idle();
            Assert.True(field.IsVisible);
            Assert.False(field.IsEnabled);
            Assert.False(service.IsRegistered);
            Assert.Empty(native.Registered);
        }
        finally { window.Close(); }
    });

    private static void AssertHotkeyTextReadable(MainWindow window)
    {
        var overlay = (Border)window.FindName("HotkeySuccessOverlay");
        Rect surface = overlay.TransformToVisual(window).TransformBounds(new Rect(overlay.RenderSize));
        foreach (string name in HotkeyFieldNames)
        {
            var field = (TextBox)window.FindName(name);
            var label = ((DockPanel)field.Parent).Children.OfType<TextBlock>().Single();
            Rect labelBounds = label.TransformToVisual(window).TransformBounds(new Rect(label.RenderSize));
            Assert.False(surface.IntersectsWith(labelBounds), $"Feedback covers {label.Text}: {surface}; label {labelBounds}");
            // Character rectangles cover displayed text, not the unused field area.
            for (int i = 0; i < field.Text.Length; i++)
            {
                Rect start = field.GetRectFromCharacterIndex(i);
                Rect end = field.GetRectFromCharacterIndex(i, trailingEdge: true);
                start.Union(end);
                Rect glyph = field.TransformToVisual(window).TransformBounds(start);
                Assert.False(surface.IntersectsWith(glyph), $"Feedback covers {label.Text} character {i}: {surface}; glyph {glyph}");
            }
        }
    }

    private static void RecordSettings(MainWindow window, string game)
    {
        string? directory = Environment.GetEnvironmentVariable("SOULSTRACKER_SETTINGS_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(directory, game + ".png")); encoder.Save(stream);
        string Bounds(FrameworkElement element) => element.TransformToVisual(window).TransformBounds(new Rect(element.RenderSize))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var overlay = (Border)window.FindName("HotkeySuccessOverlay");
        var fields = HotkeyFieldNames.Select(name =>
        {
            var field = (TextBox)window.FindName(name);
            var label = ((DockPanel)field.Parent).Children.OfType<TextBlock>().Single();
            return new { name, field.Text, field = Bounds(field), label = Bounds(label) };
        }).ToArray();
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, game + ".json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            overlay = Bounds(overlay),
            overlay.IsVisible,
            overlay.IsHitTestVisible,
            fields,
            feedback = ((TextBlock)window.FindName("HotkeySuccessStatus")).Text,
            scrollOffset = ((ScrollViewer)window.FindName("SettingsContentScrollViewer")).VerticalOffset,
            focus = (Keyboard.FocusedElement as FrameworkElement)?.Name
        }));
    }

    private sealed class Native : IWindowsGlobalHotkeyNative
    {
        internal uint? RejectedKey { get; set; }
        internal Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = [];
        public bool RegisterHotKey(nint handle, int id, uint modifiers, uint key) => key != RejectedKey && Registered.TryAdd(id, (modifiers, key));
        public bool UnregisterHotKey(nint handle, int id) => Registered.Remove(id);
    }
    private sealed class Sink : IGlobalHotkeyMessageSink
    {
        public nint WindowHandle => 1;
        public void SetMessageHandler(Func<int, nint, bool> messageHandler) { }
        public void Dispose() { }
    }
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
}
