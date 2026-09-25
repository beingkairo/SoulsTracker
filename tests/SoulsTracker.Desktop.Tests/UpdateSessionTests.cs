using System.IO;
using System.Net;
using System.Net.Http;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop.Tests;

public sealed class UpdateSessionTests
{
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
        Assert.Equal(notice, manual.IsUpdateNoticeVisible);
        Assert.Equal(notice, automatic.IsUpdateNoticeVisible);
    }

    private sealed class ResponseHandler(string body, int code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => code == 0
            ? throw new HttpRequestException("Synthetic offline")
            : Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) });
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
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker, updateReleasePageLauncher: launcher);
        await vm.InitializeAsync();
        vm.OpenUpdateProductPage();
        Assert.Null(launcher.Opened);
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
        public Uri? Opened { get; private set; }
        public bool TryOpen(Uri releasePage) { Opened = releasePage; return true; }
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
            Assert.False(vm.CanCheckForUpdates);
        }
        finally
        {
            checker.Result.TrySetResult(new(ManualReleaseUpdateStatus.Unavailable));
            await Task.WhenAll(pending, duplicate);
        }
        Assert.True(vm.CanRetryUpdateCheck);
    }

    [Fact]
    public async Task RecheckKeepsVerifiedNoticeUntilTheNewOutcomeArrives()
    {
        var checker = new ControlledChecker();
        await using var coordinator = new SerializedTrackerCoordinator(new Repository(), new Publisher());
        await using var vm = new DesktopTrackerViewModel(coordinator, manualReleaseUpdateChecker: checker);
        await vm.InitializeAsync();
        checker.Result.SetResult(new(ManualReleaseUpdateStatus.UpdateAvailable, "2.0.0"));
        await vm.CheckForUpdatesAsync();
        checker.Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = vm.CheckForUpdatesAsync();
        try { Assert.True(vm.IsUpdateNoticeVisible); }
        finally { checker.Result.SetResult(new(ManualReleaseUpdateStatus.Unavailable)); await pending; }
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
