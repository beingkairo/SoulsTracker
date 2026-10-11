using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

[Collection("Shell presentation")]
public sealed class ManualDeathControlTests
{
    [Fact]
    public Task CounterCommitsDoNotRefreshUnrelatedMainControls() => RunOnDispatcherAsync(async () =>
    {
        using var save = new TemporarySave();
        var repository = new MemoryRepository(save.Path);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var tracker = new DesktopTrackerViewModel(coordinator);
        var window = new MainWindow { DataContext = tracker, ShowInTaskbar = false };
        try
        {
            window.Show();
            await tracker.InitializeAsync();
            await FlushBindingsAsync();
            var gameSelector = (ComboBox)window.FindName("GameSelector");
            var characterSelector = (ComboBox)window.FindName("EldenRingProfileSlotSelector");
            var increment = FindButton(window, "Increase manual deaths");
            var decrement = FindButton(window, "Decrease manual deaths");
            var unrelated = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(tracker.ControlsEnabled), nameof(tracker.IsBusy), nameof(tracker.SelectedGame),
                nameof(tracker.GameChoices), nameof(tracker.SelectedEldenRingProfileSlot),
                nameof(tracker.EldenRingProfileSlots), nameof(tracker.SelectedEldenRingSaveChoice),
                nameof(tracker.EldenRingSaveChoices), nameof(tracker.SelectedBlackMythWukongSaveChoice),
                nameof(tracker.BlackMythWukongSaveChoices), nameof(tracker.SelectedLiesOfPSaveChoice),
                nameof(tracker.LiesOfPSaveChoices), nameof(tracker.EldenRingDirectoryPath),
                nameof(tracker.BlackMythWukongDirectoryPath), nameof(tracker.LiesOfPDirectoryPath),
                nameof(tracker.CanSelectEldenRingProfile), nameof(tracker.IsEldenRingSaveSelectorEnabled),
                nameof(tracker.IsWukongSaveSelectorEnabled), nameof(tracker.IsLiesOfPSaveSelectorEnabled),
                nameof(tracker.RuntimeReaderStatusText), nameof(tracker.CanChangeUpdatePreference),
                nameof(tracker.CheckForUpdatesOnStartup), nameof(tracker.PresentationControlsEnabled)
            };
            var unexpected = new List<string>();
            var countNotifications = new List<long>();
            var decrementNotifications = new List<bool>();
            var gameEnabledChanges = new List<bool>();
            var characterEnabledChanges = new List<bool>();
            tracker.PropertyChanged += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.PropertyName) || unrelated.Contains(e.PropertyName))
                    unexpected.Add(e.PropertyName ?? "<null>");
                if (e.PropertyName == nameof(tracker.ManualDeaths)) countNotifications.Add(tracker.ManualDeaths);
                if (e.PropertyName == nameof(tracker.CanDecrementManualDeaths))
                    decrementNotifications.Add(tracker.CanDecrementManualDeaths);
            };
            gameSelector.IsEnabledChanged += (_, _) => gameEnabledChanges.Add(gameSelector.IsEnabled);
            characterSelector.IsEnabledChanged += (_, _) => characterEnabledChanges.Add(characterSelector.IsEnabled);

            Invoke(increment);
            await WaitUntilAsync(() => tracker.ManualDeaths == 1 && decrement.IsEnabled);
            Invoke(decrement);
            await WaitUntilAsync(() => tracker.ManualDeaths == 0 && !decrement.IsEnabled);
            await FlushBindingsAsync();

            Assert.Equal(0, tracker.ManualDeaths);
            Assert.Equal(0, repository.State.ManualDemonsSoulsDeathCounter.Value);
            Assert.Collection(countNotifications, count => Assert.Equal(1, count), count => Assert.Equal(0, count));
            Assert.Collection(decrementNotifications, canDecrement => Assert.True(canDecrement), canDecrement => Assert.False(canDecrement));
            Assert.True(unexpected.Count == 0, string.Join(", ", unexpected));
            Assert.Empty(gameEnabledChanges);
            Assert.Empty(characterEnabledChanges);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CounterOnlyCommitsKeepGameSelectionItemsScrollAndButtonFocus() => RunOnDispatcherAsync(async () =>
    {
        using var save = new TemporarySave();
        await using var coordinator = new SerializedTrackerCoordinator(new MemoryRepository(save.Path), new NullPublisher());
        var tracker = new DesktopTrackerViewModel(coordinator);
        var window = new MainWindow { DataContext = tracker, ShowInTaskbar = false };
        try
        {
            window.Show();
            await tracker.InitializeAsync();
            await FlushBindingsAsync();
            var selector = (ComboBox)window.FindName("GameSelector");
            var scroll = (ScrollViewer)window.FindName("MainContentScrollViewer");
            var increment = FindButton(window, "Increase manual deaths");
            var decrement = FindButton(window, "Decrease manual deaths");
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            GameChoice? selected = tracker.SelectedGame;
            object items = selector.ItemsSource;
            int selectionEvents = 0, selectionNotifications = 0, automationNotifications = 0;
            selector.SelectionChanged += (_, _) => selectionEvents++;
            tracker.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(tracker.SelectedGame)) selectionNotifications++;
                if (e.PropertyName == nameof(tracker.GameSelectionAutomationName)) automationNotifications++;
            };
            scroll.ScrollToBottom();
            await FlushBindingsAsync();
            double offset = scroll.VerticalOffset;
            Assert.True(increment.Focus());
            await tracker.IncrementManualDeathsAsync();
            await FlushBindingsAsync();
            Assert.Equal("1", total.Text);
            Assert.Same(increment, System.Windows.Input.Keyboard.FocusedElement);
            Assert.Same(selected, selector.SelectedItem);
            Assert.Same(items, selector.ItemsSource);
            Assert.Equal(offset, scroll.VerticalOffset);
            Assert.True(decrement.Focus());
            Invoke(decrement);
            await WaitUntilAsync(() => total.Text == "0" && tracker.ControlsEnabled);
            await FlushBindingsAsync();
            Assert.Equal("0", total.Text);
            Assert.Same(increment, System.Windows.Input.Keyboard.FocusedElement);
            Assert.Same(selected, tracker.SelectedGame);
            Assert.Same(selected, selector.SelectedItem);
            Assert.Same(items, selector.ItemsSource);
            Assert.Equal(offset, scroll.VerticalOffset);
            Assert.Equal(0, selectionEvents);
            Assert.Equal(0, selectionNotifications);
            Assert.Equal(0, automationNotifications);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ManualButtonsWorkOnlyForDemonsSoulsAndRestoreAfterGameSwitches() => RunOnDispatcherAsync(async () =>
    {
        using var save = new TemporarySave();
        var repository = new MemoryRepository(save.Path);
        await using var coordinator = new SerializedTrackerCoordinator(repository, new NullPublisher());
        var tracker = new DesktopTrackerViewModel(coordinator, new ProfileReader(),
            blackMythWukongSaveDiscovery: new SaveDiscovery(),
            eldenRingSaveDiscovery: new SaveDiscovery(save.Path),
            liesOfPSaveDiscovery: new SaveDiscovery());
        var window = new MainWindow { DataContext = tracker, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            await FlushBindingsAsync();
            Button increment = FindButton(window, "Increase manual deaths");
            Button decrement = FindButton(window, "Decrease manual deaths");
            var total = (TextBlock)window.FindName("TotalDeathsTextBlock");
            var selector = (ComboBox)window.FindName("GameSelector");
            Assert.False(increment.IsEnabled);
            Assert.False(decrement.IsEnabled);
            await tracker.InitializeAsync();
            tracker.SetGlobalHotkeyStatus("Global hotkeys are active.");
            await FlushBindingsAsync();
            Assert.False(((TextBlock)window.FindName("GlobalHotkeyStatusTextBlock")).IsVisible);
            Assert.Equal("+1", increment.Content);
            Assert.Equal("−1", decrement.Content);
            Assert.True(increment.IsVisible);
            Assert.True(decrement.IsVisible);
            Assert.True(increment.IsEnabled);
            Assert.False(decrement.IsEnabled);
            Assert.True(increment.Focusable);
            Assert.True(decrement.Focusable);
            Assert.True(decrement.TranslatePoint(new Point(), window).X < increment.TranslatePoint(new Point(), window).X);
            Assert.Equal("0", total.Text);
            Assert.Throws<ElementNotEnabledException>(() => Invoke(decrement));
            Assert.Equal(0, repository.State.ManualDemonsSoulsDeathCounter.Value);

            Invoke(increment);
            await WaitUntilAsync(() => total.Text == "1" && decrement.IsEnabled);
            Assert.Equal(1, repository.State.ManualDemonsSoulsDeathCounter.Value);
            Invoke(decrement);
            await WaitUntilAsync(() => total.Text == "0" && !decrement.IsEnabled);
            Assert.Throws<ElementNotEnabledException>(() => Invoke(decrement));
            Assert.Equal(0, repository.State.ManualDemonsSoulsDeathCounter.Value);
            Invoke(increment);
            await WaitUntilAsync(() => total.Text == "1" && decrement.IsEnabled);

            foreach (GameChoice choice in tracker.GameChoices.Where(choice => choice.GameId != GameId.DemonsSouls))
            {
                selector.SelectedItem = choice;
                await WaitUntilAsync(() => tracker.SelectedGame?.GameId == choice.GameId && tracker.ControlsEnabled);
                await FlushBindingsAsync();
                Assert.False(increment.IsVisible);
                Assert.False(((TextBlock)window.FindName("GlobalHotkeyStatusTextBlock")).IsVisible);
                Assert.False(decrement.IsVisible);
                Assert.Equal(1, repository.State.ManualDemonsSoulsDeathCounter.Value);

                Button addMissedDeath = FindButton(window, "Add Elden Ring missed death");
                Button removeMissedDeath = FindButton(window, "Remove Elden Ring missed death");
                if (choice.GameId == GameId.EldenRing)
                {
                    tracker.ApplyRuntimeReaderResult(RuntimeGameReadResult.Synced(new RuntimeGameObservation(
                        GameId.EldenRing, new GameLifetimeDeathTotal(12), DateTimeOffset.UtcNow,
                        EffectiveDeathTotalResult.SourceIdentityFor(repository.State))));
                    await FlushBindingsAsync();
                    Assert.True(addMissedDeath.IsVisible);
                    Assert.True(removeMissedDeath.IsVisible);
                    Assert.True(addMissedDeath.IsEnabled);
                    Assert.False(removeMissedDeath.IsEnabled);
                    Assert.True(removeMissedDeath.TranslatePoint(new Point(), window).X < addMissedDeath.TranslatePoint(new Point(), window).X);
                    Invoke(addMissedDeath);
                    await WaitUntilAsync(() => total.Text == "13" && removeMissedDeath.IsEnabled);
                    Invoke(removeMissedDeath);
                    await WaitUntilAsync(() => total.Text == "12" && !removeMissedDeath.IsEnabled);
                }
                else
                {
                    Assert.False(addMissedDeath.IsVisible);
                    Assert.False(removeMissedDeath.IsVisible);
                }

                selector.SelectedItem = tracker.GameChoices.Single(game => game.GameId == GameId.DemonsSouls);
                await WaitUntilAsync(() => tracker.SelectedGame?.GameId == GameId.DemonsSouls && tracker.ControlsEnabled);
                await FlushBindingsAsync();
                Assert.True(increment.IsVisible);
                Assert.True(decrement.IsVisible);
                Assert.True(increment.IsEnabled);
                Assert.True(decrement.IsEnabled);
                Assert.Equal("1", total.Text);
                Assert.False(addMissedDeath.IsVisible);
                Assert.False(removeMissedDeath.IsVisible);
                Invoke(increment);
                await WaitUntilAsync(() => total.Text == "2" && tracker.ControlsEnabled);
                Invoke(decrement);
                await WaitUntilAsync(() => total.Text == "1" && tracker.ControlsEnabled);
            }
        }
        finally { window.Close(); }
    });

    private static Button FindButton(DependencyObject root, string accessibleName)
    {
        Button? button = Descendants(root).OfType<Button>()
            .SingleOrDefault(button => AutomationProperties.GetName(button) == accessibleName);
        Assert.True(button is not null, $"Missing actual WPF button: {accessibleName}");
        return button!;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject) continue;
            yield return dependencyObject;
            foreach (DependencyObject descendant in Descendants(dependencyObject)) yield return descendant;
        }
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static async Task FlushBindingsAsync() =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
        await FlushBindingsAsync();
    }

    private static async Task RunOnDispatcherAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private sealed class NullPublisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TemporarySave : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"souls-tracker-controls-{Guid.NewGuid():N}");
        public string Path { get; }
        public TemporarySave()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "ER0000.sl2");
            File.WriteAllBytes(Path, []);
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class SaveDiscovery(string? path = null) : ILocalSaveDiscovery
    {
        public ValueTask<IReadOnlyList<DiscoveredLocalSave>> DiscoverAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DiscoveredLocalSave>>(path is null ? [] : [new(path, "Test character save")]);
    }

    private sealed class ProfileReader : IEldenRingSaveProfileReader
    {
        public ValueTask<IReadOnlyList<EldenRingCharacterSlotMetadata>> ReadAsync(EldenRingSaveConfiguration configuration, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<EldenRingCharacterSlotMetadata>>([new(1, false, "Test character", 1)]);
    }

    private sealed class MemoryRepository(string savePath) : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; private set; } = new(
            PersistentTrackerState.CurrentSchemaVersion, GameId.DemonsSouls, OverlayConfiguration.Default,
            eldenRingNoticeAcknowledged: true,
            eldenRingSave: new EldenRingSaveConfiguration(savePath, 1));
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            State = state;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
