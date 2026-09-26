using System.IO;
using System.Net;
using System.Net.Http;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class UpdateSessionTests
{
    [Theory]
    [InlineData("https://beingkairo.com/tools/souls-tracker/", true)]
    [InlineData("http://beingkairo.com/tools/souls-tracker/", false)]
    [InlineData("https://beingkairo.com/tools/souls-tracker/?next=other", false)]
    [InlineData("https://beingkairo.com/tools/souls-tracker/#other", false)]
    [InlineData("https://beingkairo.com:443/tools/souls-tracker/", false)]
    [InlineData("https://beingkairo.com/tools/other/../souls-tracker/", false)]
    [InlineData("https://beingkairo.com@other.example/tools/souls-tracker/", false)]
    [InlineData("https://github.com/beingkairo/SoulsTracker/releases", false)]
    [InlineData("/tools/souls-tracker/", false)]
    public void ShellLaunchAllowlistAcceptsOnlyExactHttpsProductPage(string value, bool allowed)
    {
        var page = new Uri(value, UriKind.RelativeOrAbsolute);
        Assert.Equal(allowed, ShellUpdateReleasePageLauncher.IsAllowed(page));
        if (!allowed) Assert.False(new ShellUpdateReleasePageLauncher().TryOpen(page));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualNewerChecksNeverNotifyEvenWithEnabledPreference(bool enabled)
    {
        var repository = new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: enabled) };
        var checker = new ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
        await using var coordinator = new SerializedTrackerCoordinator(repository, new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker);
        await vm.InitializeAsync();
        await vm.CheckForUpdatesAsync();
        await vm.SetCheckForUpdatesOnStartupAsync(!enabled);
        await vm.CheckForUpdatesAsync();
        Assert.False(vm.IsUpdateNoticeVisible);
        Assert.Equal("New version out!", vm.UpdateCheckStatus);
        Assert.Equal(2, checker.Calls);
    }

    [Fact]
    public async Task NewSessionCanNotifyAgainAndToggleUsesInitializationSnapshot()
    {
        var repository = new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true) };
        for (int session = 0; session < 2; session++)
        {
            var checker = new ControlledChecker();
            checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
            await using var coordinator = new SerializedTrackerCoordinator(repository, new Publisher());
            await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker);
            await vm.InitializeAsync();
            await vm.SetCheckForUpdatesOnStartupAsync(false);
            vm.StartStartupUpdateCheck();
            Assert.True(vm.IsUpdateNoticeVisible);
            Assert.Equal(1, checker.Calls);
            vm.DismissUpdateNotice();
            vm.StartStartupUpdateCheck();
            Assert.Equal(1, checker.Calls);
            await vm.CheckForUpdatesAsync();
            Assert.False(vm.IsUpdateNoticeVisible);
            await vm.SetCheckForUpdatesOnStartupAsync(true);
        }
    }

    [Fact]
    public async Task StartupCancellationDuringGraceCannotNotifyOrLeaveTimer()
    {
        var clock = new UpdatePresentationClock();
        var checker = new ControlledChecker();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true) }, new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, timeProvider: clock);
        await vm.InitializeAsync();
        vm.StartStartupUpdateCheck();
        clock.Advance(299);
        await vm.StopUpdateChecksAsync();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
        clock.Advance(1000);
        Assert.False(vm.IsUpdateNoticeVisible);
        Assert.False(vm.IsUpdateProgressVisible);
        Assert.Equal(0, clock.UndisposedTimers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReleaseActionUsesProductPageWithoutChangingVerifiedResult(bool succeeds)
    {
        var checker = new ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0", new Uri("https://github.com/beingkairo/SoulsTracker/releases/tag/v2.0.0")));
        var launcher = new Launcher { Succeeds = succeeds };
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, updateReleasePageLauncher: launcher);
        await vm.InitializeAsync();
        await vm.CheckForUpdatesAsync();
        var before = (vm.UpdateCurrentVersion, vm.UpdateLatestVersion, vm.UpdateCheckStatus);
        Assert.Null(launcher.Opened);
        vm.OpenAvailableUpdateReleasePage();
        Assert.Equal(new Uri("https://beingkairo.com/tools/souls-tracker/"), launcher.Opened);
        Assert.Equal(before, (vm.UpdateCurrentVersion, vm.UpdateLatestVersion, vm.UpdateCheckStatus));
        Assert.Equal(!succeeds, vm.UpdatePageActionError.Length > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FastRecheckPreservesCompletedPresentationAndSuppressesDuplicates(bool startup)
    {
        var checker = new ControlledChecker();
        var clock = new UpdatePresentationClock();
        var repository = new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: startup) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, timeProvider: clock);
        await vm.InitializeAsync();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpToDate, "1.3.2"));
        await vm.CheckForUpdatesAsync();
        string? status = vm.UpdateCheckStatus;
        checker.Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new List<string?>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.UpdateCheckStatus)) statuses.Add(vm.UpdateCheckStatus); };
        if (startup) vm.StartStartupUpdateCheck();
        Task check = vm.CheckForUpdatesAsync();
        try
        {
            Assert.Equal(status, vm.UpdateCheckStatus);
            Assert.True(vm.CanCheckForUpdates);
            Assert.Same(check, vm.CheckForUpdatesAsync());
            Assert.Equal(2, checker.Calls);
            clock.Advance(299);
            Assert.False(vm.IsUpdateProgressVisible);
            Assert.Equal(1, clock.UndisposedTimers);
        }
        finally
        {
            checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpToDate, "1.3.2"));
            await check;
        }
        Assert.Empty(statuses);
        Assert.Equal(0, clock.UndisposedTimers);
        clock.Advance(1000);
        Assert.False(vm.IsUpdateProgressVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowOperationDisclosesProgressOnceAtThresholdAndDrainsTimer(bool startup)
    {
        var clock = new UpdatePresentationClock();
        var checker = new ControlledChecker();
        var repository = new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: startup) };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, timeProvider: clock);
        await vm.InitializeAsync();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int checkingCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.UpdateCheckStatus) && vm.UpdateCheckStatus == "Checking for updates…")
            {
                checkingCount++;
                shown.TrySetResult();
            }
        };
        if (startup) vm.StartStartupUpdateCheck();
        Task operation = vm.CheckForUpdatesAsync();
        try
        {
            Assert.Equal(1, checker.Calls);
            Assert.True(vm.IsCheckingForUpdates);
            clock.Advance(299);
            Assert.False(vm.IsUpdateProgressVisible);
            Assert.Equal("Not checked yet.", vm.UpdateCheckStatus);
            clock.Advance(1);
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(vm.IsUpdateProgressVisible);
            clock.Advance(1000);
            Assert.Equal(1, checkingCount);
        }
        finally
        {
            checker.Result.TrySetResult(new(ManualReleaseUpdateStatus.UpToDate, "1.3.2"));
            await operation;
        }
        Assert.Equal("All up to date.", vm.UpdateCheckStatus);
        Assert.False(vm.IsUpdateProgressVisible);
        Assert.Equal(0, clock.UndisposedTimers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringGraceDrainsPresentationWithoutShowingProgress(bool shutdown)
    {
        var clock = new UpdatePresentationClock();
        var checker = new ControlledChecker();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, timeProvider: clock);
        await vm.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        bool progressShown = false;
        vm.PropertyChanged += (_, _) => progressShown |= vm.IsUpdateProgressVisible;
        Task operation = vm.CheckForUpdatesAsync(cancellation.Token);
        clock.Advance(299);
        if (shutdown) await vm.StopUpdateChecksAsync();
        else cancellation.Cancel();
        await operation;
        Assert.Equal(0, clock.UndisposedTimers);
        clock.Advance(1000);
        Assert.False(progressShown);
        Assert.False(vm.IsCheckingForUpdates);
        Assert.Equal(!shutdown, vm.CanCheckForUpdates);
    }

    [Fact]
    public async Task ShutdownWaitsForCheckerDrainAndRejectsLateSuccessfulResult()
    {
        var checker = new DrainingChecker();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker);
        await vm.InitializeAsync();
        Task check = vm.CheckForUpdatesAsync();
        Task stop = vm.StopUpdateChecksAsync();
        Assert.True(checker.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
        await Task.WhenAll(check, stop);
        Assert.False(vm.IsUpdateNoticeVisible);
        Assert.Equal("Error", vm.UpdateCheckTone);
        Assert.Equal("Unavailable", vm.UpdateLatestVersion);
    }

    private sealed class DrainingChecker : IManualReleaseUpdateChecker
    {
        public TaskCompletionSource<ManualReleaseUpdateResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public ValueTask<ManualReleaseUpdateResult> CheckAsync(string installedVersion, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return new(Result.Task);
        }
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v2.0.0\"}", 200, "Available", true)]
    [InlineData("{\"tag_name\":\"v1.0.0\"}", 200, "Current", false)]
    [InlineData("{\"tag_name\":\"v0.9.0\"}", 200, "Current", false)]
    [InlineData("null", 200, "Error", false)]
    [InlineData("[]", 200, "Error", false)]
    [InlineData("{\"tag_name\":false}", 200, "Error", false)]
    [InlineData("", 429, "Error", false)]
    [InlineData("", 403, "Error", false)]
    [InlineData("", 500, "Error", false)]
    [InlineData("offline", 0, "Error", false)]
    public async Task ManualAndStartupShareRealCheckerOutcomeMapping(string body, int code, string tone, bool notice)
    {
        using var client = new HttpClient(new ResponseHandler(body, code));
        var checker = new GitHubLatestReleaseUpdateChecker(client);
        await using var manualCoordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        await using var autoCoordinator = new SerializedTrackerCoordinator(new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true) }, new Publisher());
        await using var manual = new DesktopTrackerViewModel(manualCoordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "v1.0.0+local");
        await using var automatic = new DesktopTrackerViewModel(autoCoordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "v1.0.0+local");
        await manual.InitializeAsync();
        await automatic.InitializeAsync();
        await manual.CheckForUpdatesAsync();
        automatic.StartStartupUpdateCheck();
        await automatic.CheckForUpdatesAsync();
        Assert.Equal(tone, manual.UpdateCheckTone);
        Assert.Equal(tone, automatic.UpdateCheckTone);
        Assert.Equal(manual.UpdateCheckStatus, automatic.UpdateCheckStatus);
        Assert.Equal(manual.UpdateLatestVersion, automatic.UpdateLatestVersion);
        Assert.Equal(manual.AvailableUpdateReleasePage, automatic.AvailableUpdateReleasePage);
        Assert.False(manual.IsUpdateNoticeVisible);
        Assert.Equal(notice, automatic.IsUpdateNoticeVisible);
    }

    [Theory]
    [InlineData("null", 200)]
    [InlineData("", 429)]
    [InlineData("", 403)]
    [InlineData("", 500)]
    [InlineData("offline", 0)]
    public async Task CheckActionRetriesSafeFailuresWithFreshRequest(string body, int code)
    {
        var handler = new ResponseHandler(body, code);
        using var client = new HttpClient(handler);
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: new GitHubLatestReleaseUpdateChecker(client), installedVersionProvider: () => "1.0.0");
        await vm.InitializeAsync();
        await vm.CheckForUpdatesAsync();
        Assert.Equal("Error", vm.UpdateCheckTone);
        Assert.True(vm.CanCheckForUpdates);
        handler.Body = "{\"tag_name\":\"v1.0.0\"}";
        handler.Code = 200;
        await vm.CheckForUpdatesAsync();
        Assert.Equal(2, handler.Calls);
        Assert.Equal("Current", vm.UpdateCheckTone);
        Assert.Equal("All up to date.", vm.UpdateCheckStatus);
    }

    private sealed class ResponseHandler(string body, int code) : HttpMessageHandler
    {
        public string Body { get; set; } = body;
        public int Code { get; set; } = code;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Code == 0
                ? throw new HttpRequestException("Synthetic offline")
                : Task.FromResult(new HttpResponseMessage((HttpStatusCode)Code) { Content = new StringContent(Body) });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupUsesCommittedOptInOnceAndShutdownDrainsPendingCheck(bool enabled)
    {
        var repository = new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: enabled) };
        var checker = new ControlledChecker();
        await using var coordinator = new SerializedTrackerCoordinator(repository, new Publisher());
        var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker);
        await vm.InitializeAsync();
        vm.StartStartupUpdateCheck();
        vm.StartStartupUpdateCheck();
        Assert.Equal(enabled ? 1 : 0, checker.Calls);
        Assert.True(vm.ControlsEnabled);
        await vm.SetCheckForUpdatesOnStartupAsync(!enabled);
        Assert.Equal(enabled ? 1 : 0, checker.Calls);
        await vm.StopUpdateChecksAsync();
        Assert.False(vm.IsCheckingForUpdates);
        Assert.False(vm.CanCheckForUpdates);
        Assert.False(vm.IsUpdateNoticeVisible);
        await vm.CheckForUpdatesAsync();
        Assert.Equal(enabled ? 1 : 0, checker.Calls);
    }

    [Fact]
    public async Task PreferenceSaveFailureKeepsCommittedValueAndReportsFailure()
    {
        var repository = new Repository { FailSave = true };
        await using var coordinator = new SerializedTrackerCoordinator(repository, new Publisher());
        var vm = new DesktopTrackerViewModel(coordinator);
        await vm.InitializeAsync();
        await vm.SetCheckForUpdatesOnStartupAsync(true);
        Assert.False(vm.CheckForUpdatesOnStartup);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task VerifiedNewerNoticeOpensOnlyConstantProductPageAndDismissesForSession()
    {
        var checker = new ControlledChecker();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0", new Uri("https://github.com/beingkairo/SoulsTracker/releases")));
        var launcher = new Launcher();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true) }, new Publisher());
        var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, updateReleasePageLauncher: launcher);
        await vm.InitializeAsync();
        vm.OpenUpdateProductPage();
        Assert.Null(launcher.Opened);
        vm.StartStartupUpdateCheck();
        await vm.CheckForUpdatesAsync();
        Assert.True(vm.IsUpdateNoticeVisible);
        Assert.Null(launcher.Opened);
        vm.OpenUpdateProductPage();
        Assert.Equal(new Uri("https://beingkairo.com/tools/souls-tracker/"), launcher.Opened);
        vm.DismissUpdateNotice();
        await vm.CheckForUpdatesAsync();
        Assert.False(vm.IsUpdateNoticeVisible);
        await vm.StopUpdateChecksAsync();
    }

    private sealed class Launcher : IUpdateReleasePageLauncher
    {
        public bool Succeeds { get; init; } = true;
        public Uri? Opened { get; private set; }
        public bool TryOpen(Uri releasePage) { Opened = releasePage; return Succeeds; }
    }

    [Fact]
    public async Task RecheckRetainsLatestContextWhileSuppressingDuplicateRequests()
    {
        var checker = new ControlledChecker();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, installedVersionProvider: () => "v1.0.0+build");
        await vm.InitializeAsync();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpToDate, "1.0.0"));
        await vm.CheckForUpdatesAsync();
        checker.Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = vm.CheckForUpdatesAsync();
        Task duplicate = vm.CheckForUpdatesAsync();
        try
        {
            Assert.True(vm.IsCheckingForUpdates);
            Assert.Equal("1.0.0", vm.UpdateLatestVersion);
            Assert.Equal("1.0.0", vm.UpdateCurrentVersion);
            Assert.Equal(2, checker.Calls);
            Assert.True(vm.CanCheckForUpdates);
        }
        finally
        {
            checker.Result.TrySetResult(new(ManualReleaseUpdateStatus.Unavailable));
            await Task.WhenAll(pending, duplicate);
        }
        Assert.True(vm.CanCheckForUpdates);
    }

    [Fact]
    public async Task RecheckKeepsStartupNoticeUntilSessionDismissal()
    {
        var checker = new ControlledChecker();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository { State = new(1, GameId.DemonsSouls, OverlayConfiguration.Default, checkForUpdatesOnStartup: true) }, new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker);
        await vm.InitializeAsync();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
        vm.StartStartupUpdateCheck();
        await vm.CheckForUpdatesAsync();
        checker.Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = vm.CheckForUpdatesAsync();
        try { Assert.True(vm.IsUpdateNoticeVisible); }
        finally { checker.Result.SetResult(new(ManualReleaseUpdateStatus.Unavailable)); await pending; }
        Assert.True(vm.IsUpdateNoticeVisible);
        Assert.Contains("2.0.0", vm.UpdateNoticeText, StringComparison.Ordinal);
        vm.DismissUpdateNotice();
        Assert.False(vm.IsUpdateNoticeVisible);
    }

    internal sealed class ControlledChecker : IManualReleaseUpdateChecker
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<ManualReleaseUpdateResult> Result { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ManualReleaseUpdateResult> CheckAsync(string installedVersion, CancellationToken cancellationToken = default)
        {
            Calls++;
            return await Result.Task.WaitAsync(cancellationToken);
        }
    }

    internal sealed class Publisher : ITrackerStateChangePublisher
    {
        public Task PublishAsync(TrackerStateChanged notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    internal sealed class Repository : ITrackerStateRepository
    {
        public PersistentTrackerState State { get; set; } = PersistentTrackerState.Default;
        public bool FailSave { get; set; }
        public Task<TrackerStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(TrackerStateLoadResult.Loaded(State));
        public Task SaveAsync(PersistentTrackerState state, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Synthetic save failure");
            State = state;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
