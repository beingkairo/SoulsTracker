using System.ComponentModel;
using System.IO;
using System.Windows;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;
using SoulsTracker.Overlay;

namespace SoulsTracker.Desktop;

/// <summary>Composes the desktop surface with the approved local state command path.</summary>
public partial class App : System.Windows.Application, IDisposable
{
    private readonly DesktopShutdownCoordinator shutdownCoordinator;
    private readonly DesktopStartupController singleInstanceStartup;
    private SerializedTrackerCoordinator? coordinator;
    private DesktopGlobalHotkeyService? globalHotkeys;
    private bool mainWindowCloseRequested;
    private SecureOverlayService? overlayService;
    private OverlayStateChangePublisher? overlayPublisher;
    private TextExportStatePublisher? textExportPublisher;
    private RuntimeGameReaderCoordinator? runtimeReaders;
    private CancellationTokenSource? runtimeReaderCancellation;
    private AutomatedDeathSoundNotifier? automatedDeathSoundNotifier;

    public App()
    {
        singleInstanceStartup = new DesktopStartupController(new WindowsSingleInstanceLeaseFactory());
        shutdownCoordinator = new DesktopShutdownCoordinator(
            DisposeGlobalHotkeysAsync,
            DisposeOverlayServiceAsync,
            DisposeCoordinatorAsync,
            singleInstanceStartup);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            DesktopStartupDecision decision = await singleInstanceStartup.StartAsync(StartTrackerAsync);
            if (decision.CanStart)
            {
                return;
            }

            System.Windows.MessageBox.Show(decision.UserMessage!, "SoulsTracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private async Task StartTrackerAsync()
    {
        string applicationDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SoulsTracker");
        overlayPublisher = new OverlayStateChangePublisher();
        textExportPublisher = new TextExportStatePublisher();
        var repository = new SqliteTrackerStateRepository(applicationDataRoot, "tracker.db");
        coordinator = new SerializedTrackerCoordinator(repository, new CompositeTrackerStateChangePublisher(overlayPublisher, textExportPublisher), new SqliteConfirmedLegacyImportCommitter(repository));

        var viewModel = new DesktopTrackerViewModel(coordinator);
        textExportPublisher.WriteCompleted += (_, succeeded) => Dispatcher.InvokeAsync(() => viewModel.SetTextExportStatus(succeeded));
        viewModel.ConfigureDeathSoundPlayback(new WpfDeathSoundPlayer());
        automatedDeathSoundNotifier = new AutomatedDeathSoundNotifier(new WpfDeathSoundPlayer());
        var locator = new ApprovedLegacyImportLocationLocator();
        viewModel.ConfigureLegacyImport(new LegacyImportViewModel(new LegacyImportWorkflow(locator, new ApprovedLegacyImportPreflight(locator), coordinator), viewModel.ApplyImportedCommittedState));
        var window = new MainWindow { DataContext = viewModel };
        window.Closing += MainWindow_Closing;
        MainWindow = window;
        window.Show();
        await viewModel.InitializeAsync();
        if (!mainWindowCloseRequested && viewModel.CurrentState is not null)
        {
            viewModel.LegacyImport!.OfferIfEligible(viewModel.CurrentState);
        }
        if (!mainWindowCloseRequested && viewModel.ControlsEnabled)
        {
            try
            {
                overlayService = new SecureOverlayService(coordinator, new OverlayEndpointAccessFactory());
                await overlayService.StartAsync();
                overlayPublisher.Attach(overlayService);
                viewModel.SetOverlayUrls(overlayService.TotalDeathsUrl, overlayService.BossListUrl);
                viewModel.SetOverlayReady();
            }
            catch { viewModel.SetOverlayUnavailable(); }
        }

        if (!mainWindowCloseRequested && viewModel.ControlsEnabled)
        {
            StartGlobalHotkeys(window, viewModel);
        }

        if (!mainWindowCloseRequested && viewModel.ControlsEnabled)
        {
            runtimeReaders = new RuntimeGameReaderCoordinator([
                new DarkSoulsRemasteredActiveCharacterDeathReader(
                    new ExactNameDarkSoulsRemasteredProcessEnumerator(),
                    new WindowsReadOnlyProcessAttachmentFactory()),
                new DarkSoulsIIScholarActiveCharacterDeathReader(
                    new ExactNameDarkSoulsIIScholarProcessEnumerator(),
                    new WindowsReadOnlyProcessAttachmentFactory(),
                    new ExactDarkSoulsIIScholarIdentityValidator(new ProcessModuleFileIdentity(
                        "DarkSoulsII.exe",
                        "1,0,3,0",
                        "1,0,3,0",
                        "0045931B8914504531B7864A9488D396DC50CBAF524964016E1D69C3D1173131"))),
                new DarkSoulsIIIActiveCharacterDeathReader(
                    new ExactNameDarkSoulsIIIProcessEnumerator(),
                    new WindowsReadOnlyProcessAttachmentFactory(),
                    new ExactDarkSoulsIIIIdentityValidator(new ProcessModuleFileIdentity(
                        "DarkSoulsIII.exe",
                        "1.15.2.0",
                        "1.15.2.0",
                        "EF5E07C55222F14FFDDECF2C724A0C2A95CEF0D4DA0E075B0DE0BB108B69498C"))),
                new SekiroActiveCharacterDeathReader(
                    new ExactNameSekiroProcessEnumerator(),
                    new WindowsReadOnlyProcessAttachmentFactory(),
                    new ExactSekiroIdentityValidator(new ProcessModuleFileIdentity(
                        "sekiro.exe",
                        "1.6.0.0",
                        "1.6.0.0",
                        "637ACA527538C0EC6E1F136C8ED66046E95DFBDBB1F51926E134D9916398B856"))),
            ]);
            runtimeReaderCancellation = new CancellationTokenSource();
            _ = PollRuntimeReadersAsync(viewModel, runtimeReaderCancellation.Token);
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (mainWindowCloseRequested)
        {
            return;
        }

        e.Cancel = true;
        mainWindowCloseRequested = true;
        if (sender is MainWindow window)
        {
            window.DisposeLiveOverlayPreview();
        }
        await shutdownCoordinator.RequestApplicationShutdownAsync(Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Dispose();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    public void Dispose()
    {
        shutdownCoordinator.Dispose();
        GC.SuppressFinalize(this);
    }

    private void StartGlobalHotkeys(MainWindow window, DesktopTrackerViewModel viewModel)
    {
        if (mainWindowCloseRequested || !viewModel.ControlsEnabled)
        {
            return;
        }

        HwndGlobalHotkeyMessageSink? messageSink = null;
        DesktopGlobalHotkeyService? hotkeys = null;
        try
        {
            messageSink = new HwndGlobalHotkeyMessageSink(window);
            hotkeys = new DesktopGlobalHotkeyService(messageSink, new WindowsGlobalHotkeyNative(), viewModel);
            GlobalHotkeySettings storedSettings = ToDesktopHotkeys(viewModel.CurrentState!.ManualBloodborneHotkeys);
            _ = hotkeys.Start(storedSettings);
            globalHotkeys = hotkeys;
            viewModel.ConfigureGlobalHotkeys(hotkeys.ActiveSettings, async settings =>
                {
                    GlobalHotkeySettings previous = hotkeys.ActiveSettings;
                    GlobalHotkeyRegistrationResult replacement = hotkeys.Replace(settings);
                    if (!replacement.IsRegistered) return replacement;
                    try
                    {
                        await coordinator!.SetManualBloodborneHotkeysAsync(ToDomainHotkeys(settings));
                        return replacement;
                    }
                    catch
                    {
                        return hotkeys.Replace(previous).IsRegistered
                            ? GlobalHotkeyRegistrationResult.SaveFailed
                            : GlobalHotkeyRegistrationResult.UnavailableWithoutRestore;
                    }
                });
        }
        catch
        {
            hotkeys?.Dispose();
            messageSink?.Dispose();
            viewModel.SetGlobalHotkeyStatus(GlobalHotkeyRegistrationResult.Unavailable.StatusMessage);
        }
    }

    private ValueTask DisposeGlobalHotkeysAsync()
    {
        try
        {
            globalHotkeys?.Dispose();
        }
        finally
        {
            globalHotkeys = null;
        }

        return ValueTask.CompletedTask;
    }

    private static GlobalHotkeySettings ToDesktopHotkeys(SoulsTracker.Domain.ManualBloodborneHotkeyConfiguration source)
    {
        if (GlobalHotkeyBinding.TryFromPersisted(source.IncrementModifiers, source.IncrementVirtualKey, out GlobalHotkeyBinding? increment) &&
            GlobalHotkeyBinding.TryFromPersisted(source.DecrementModifiers, source.DecrementVirtualKey, out GlobalHotkeyBinding? decrement))
        {
            return new(increment!, decrement!);
        }
        return GlobalHotkeySettings.Default;
    }
    private static SoulsTracker.Domain.ManualBloodborneHotkeyConfiguration ToDomainHotkeys(GlobalHotkeySettings source) => new(source.Increment.Modifiers, source.Increment.VirtualKey, source.Decrement.Modifiers, source.Decrement.VirtualKey);

    private async ValueTask DisposeOverlayServiceAsync()
    {
        try
        {
            if (overlayService is not null)
            {
                await overlayService.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            overlayService = null;
        }
    }

    private async ValueTask DisposeCoordinatorAsync()
    {
        try
        {
            runtimeReaderCancellation?.Cancel();
            runtimeReaderCancellation?.Dispose();
            runtimeReaderCancellation = null;
            runtimeReaders = null;
            automatedDeathSoundNotifier = null;
            if (coordinator is not null)
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            coordinator = null;
            overlayPublisher = null;
        }
    }

    private async Task PollRuntimeReadersAsync(DesktopTrackerViewModel viewModel, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                RuntimeGameReadResult? result = await runtimeReaders!
                    .PollAsync(viewModel.CurrentState?.SelectedGameId, cancellationToken)
                    .ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    viewModel.ApplyRuntimeReaderResult(result);
                    if (viewModel.CurrentState is { } currentState)
                    {
                        textExportPublisher?.PublishRuntimeObservation(currentState, result);
                        automatedDeathSoundNotifier?.Observe(currentState.SelectedGameId, result, currentState.DeathSound);
                    }
                    overlayService?.PublishRuntimeObservation(result?.Observation);
                });
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
