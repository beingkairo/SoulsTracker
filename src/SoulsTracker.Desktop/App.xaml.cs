using System.ComponentModel;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Composes the desktop surface with the approved local state command path.</summary>
public partial class App : System.Windows.Application, IDisposable
{
    private readonly DesktopShutdownCoordinator shutdownCoordinator;
    private readonly DesktopStartupController singleInstanceStartup;
    private SerializedTrackerCoordinator? coordinator;
    private DesktopTrackerViewModel? trackerViewModel;
    private DesktopGlobalHotkeyService? globalHotkeys;
    private bool mainWindowCloseRequested;
    private bool finalShutdownRequested;
    private TextExportStatePublisher? textExportPublisher;
    private RuntimeGameReaderCoordinator? runtimeReaders;
    private EldenRingSaveDeathReader? eldenRingSaveReader;
    private BlackMythWukongSaveDeathReader? blackMythWukongSaveReader;
    private LiesOfPSaveDeathReader? liesOfPSaveReader;
    private CancellationTokenSource? runtimeReaderCancellation;
    private Task? runtimeReaderPollingTask;
    private readonly RuntimePublicationSession runtimePublication = new();
    private HostedOverlayConnection? hostedConnection;
    private Task? startupTask;
    private readonly CancellationTokenSource startupCancellation = new();

    private DesktopDataRootSelection? dataRootSelection;

    public App()
    {
        singleInstanceStartup = new DesktopStartupController(new WindowsSingleInstanceLeaseFactory());
        shutdownCoordinator = new DesktopShutdownCoordinator(
            stopInputsAsync: DisposeGlobalHotkeysAsync,
            drainProducersAsync: DisposeCoordinatorAsync,
            drainOutputsAsync: DisposeOutputsAsync,
            new DispatcherBoundDisposable(Dispatcher, singleInstanceStartup),
            cancelPending: () => runtimeReaderCancellation?.Cancel());
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            dataRootSelection = DesktopDataRootResolver.Resolve(
                e.Args,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            DesktopStartupDecision decision = await singleInstanceStartup.StartAsync(StartTrackerAsync);
            if (decision.CanStart)
            {
                return;
            }

            System.Windows.MessageBox.Show(decision.UserMessage!, "SoulsTracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
        catch (ArgumentException)
        {
            System.Windows.MessageBox.Show(
                "SoulsTracker could not start with the requested development data folder.",
                "SoulsTracker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
        catch
        {
            mainWindowCloseRequested = true;
            await shutdownCoordinator.RequestApplicationShutdownAsync(FinalShutdown);
        }
    }

    private Task StartTrackerAsync() => startupTask = StartTrackerCoreAsync();

    private async Task StartTrackerCoreAsync()
    {
        DesktopDataRootSelection stateSelection = dataRootSelection ?? throw new InvalidOperationException("The desktop data root was not initialized.");
        hostedConnection = new HostedOverlayConnection(Dispatcher,
            new HostedPublisherConfigurationStore(Path.Combine(stateSelection.RootPath, "hosted-pairing.private"),
                new CurrentUserDpapiSecretProtector(), HostedProductionOrigins.Approved));
        textExportPublisher = new TextExportStatePublisher();
        var repository = new SqliteTrackerStateRepository(stateSelection.RootPath, "tracker.db");
        coordinator = new SerializedTrackerCoordinator(repository,
            new DesktopStateChangePublisher(Dispatcher, runtimePublication,
                new CompositeTrackerStateChangePublisher(hostedConnection, textExportPublisher)),
            new SqliteConfirmedLegacyImportCommitter(repository));

        var viewModel = new DesktopTrackerViewModel(coordinator);
        trackerViewModel = viewModel;
        viewModel.ConfigureHostedOverlay(hostedConnection);

        textExportPublisher.WriteCompleted += (_, succeeded) => Dispatcher.InvokeAsync(() =>
        {
            if (!mainWindowCloseRequested) viewModel.SetTextExportStatus(succeeded);
        });

        if (!stateSelection.IsDevelopmentOverride)
        {
            var locator = new ApprovedLegacyImportLocationLocator();
            viewModel.ConfigureLegacyImport(new LegacyImportViewModel(new LegacyImportWorkflow(locator, new ApprovedLegacyImportPreflight(locator), coordinator), viewModel.ApplyImportedCommittedState));
        }
        var window = new MainWindow { DataContext = viewModel };
        window.ConfigureAppearancePreview(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoulsTracker"));
        window.Closing += MainWindow_Closing;
        MainWindow = window;
        window.Show();
        await viewModel.InitializeAsync(startupCancellation.Token);
        if (!mainWindowCloseRequested && viewModel.CurrentState is { } initializedState)
        {
            runtimePublication.SelectState(initializedState);
            await hostedConnection.InitializeAsync(initializedState);
        }
        if (!mainWindowCloseRequested && viewModel.CurrentState is not null && viewModel.LegacyImport is not null)
        {
            viewModel.LegacyImport!.OfferIfEligible(viewModel.CurrentState);
        }


        if (!mainWindowCloseRequested && viewModel.ControlsEnabled)
        {
            StartGlobalHotkeys(window, viewModel);
        }

        if (!mainWindowCloseRequested && viewModel.ControlsEnabled)
        {
            eldenRingSaveReader = new EldenRingSaveDeathReader();
            blackMythWukongSaveReader = new BlackMythWukongSaveDeathReader();
            liesOfPSaveReader = new LiesOfPSaveDeathReader();
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
                new BloodborneActiveCharacterDeathReader(
                    new ExactNameBloodborneProcessEnumerator(),
                    new WindowsReadOnlyProcessAttachmentFactory()),
                eldenRingSaveReader,
                blackMythWukongSaveReader,
                liesOfPSaveReader,
            ]);
            runtimeReaderCancellation = new CancellationTokenSource();
            runtimeReaderPollingTask = PollRuntimeReadersAsync(viewModel, runtimeReaderCancellation.Token);
        }
        if (!mainWindowCloseRequested) viewModel.StartStartupUpdateCheck();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (mainWindowCloseRequested)
        {
            e.Cancel = !finalShutdownRequested;
            return;
        }

        e.Cancel = true;
        mainWindowCloseRequested = true;
        try { await shutdownCoordinator.RequestApplicationShutdownAsync(FinalShutdown); }
        catch { /* Components have been awaited; final shutdown already ran. */ }
    }

    private void FinalShutdown()
    {
        finalShutdownRequested = true;
        Shutdown();
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
            GlobalHotkeySettings storedSettings = ToDesktopHotkeys(viewModel.CurrentState!.GlobalHotkeys);
            _ = hotkeys.Start(storedSettings);
            globalHotkeys = hotkeys;
            viewModel.ConfigureGlobalHotkeys(hotkeys.ActiveSettings, async settings =>
                {
                    GlobalHotkeySettings previous = hotkeys.ActiveSettings;
                    GlobalHotkeyRegistrationResult replacement = hotkeys.Replace(settings);
                    if (!replacement.IsRegistered) return replacement;
                    try
                    {
                        await coordinator!.SetGlobalHotkeysAsync(ToDomainHotkeys(settings));
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

    private async ValueTask DisposeGlobalHotkeysAsync()
    {
        startupCancellation.Cancel();
        if (MainWindow is not null) MainWindow.IsEnabled = false;
        hostedConnection?.StopSetup();
        try
        {
            globalHotkeys?.Dispose();
        }
        finally
        {
            globalHotkeys = null;
        }

        if (trackerViewModel is not null) await trackerViewModel.DisposeAsync();
    }

    private static GlobalHotkeySettings ToDesktopHotkeys(SoulsTracker.Domain.GlobalHotkeyConfiguration source)
    {
        if (GlobalHotkeyBinding.TryFromPersisted(source.IncrementModifiers, source.IncrementVirtualKey, out GlobalHotkeyBinding? increment) &&
            GlobalHotkeyBinding.TryFromPersisted(source.DecrementModifiers, source.DecrementVirtualKey, out GlobalHotkeyBinding? decrement))
        {
            return new(increment!, decrement!);
        }
        return GlobalHotkeySettings.Default;
    }
    private static SoulsTracker.Domain.GlobalHotkeyConfiguration ToDomainHotkeys(GlobalHotkeySettings source) => new(source.Increment.Modifiers, source.Increment.VirtualKey, source.Decrement.Modifiers, source.Decrement.VirtualKey);


    private async ValueTask DisposeCoordinatorAsync()
    {
        if (startupTask is not null)
        {
            try { await startupTask.ConfigureAwait(false); }
            catch { /* Failed startup still requires complete disposal of created owners. */ }
        }
        CancellationTokenSource? readerCancellation = runtimeReaderCancellation;
        Task? pollingTask = runtimeReaderPollingTask;
        try
        {
            // Stop polling before completing the publisher so its final callback
            // cannot enqueue work after the publisher has drained.
            readerCancellation?.Cancel();
            try
            {
                if (pollingTask is not null)
                {
                    await pollingTask.ConfigureAwait(false);
                }
            }
            catch
            {
                // A terminated reader must not skip draining committed producers.
            }
            finally
            {
                readerCancellation?.Dispose();
                runtimeReaderCancellation = null;
                runtimeReaderPollingTask = null;
                runtimeReaders = null;
                eldenRingSaveReader = null;
                blackMythWukongSaveReader = null;
                liesOfPSaveReader = null;
            }

            if (coordinator is not null)
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            coordinator = null;
        }
    }

    private async ValueTask DisposeOutputsAsync()
    {
        try
        {
            Task hosted = Dispatcher.CheckAccess()
                ? hostedConnection?.DisposeAsync().AsTask() ?? Task.CompletedTask
                : Dispatcher.InvokeAsync(() => hostedConnection?.DisposeAsync().AsTask() ?? Task.CompletedTask).Task.Unwrap();
            Task text = textExportPublisher?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            await Task.WhenAll(hosted, text).ConfigureAwait(false);
        }
        finally
        {
            hostedConnection = null;
            textExportPublisher = null;
            startupCancellation.Dispose();
        }
    }

    private async Task PollRuntimeReadersAsync(DesktopTrackerViewModel viewModel, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await Dispatcher.InvokeAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PersistentTrackerState currentState = runtimePublication.CurrentState!;
                    eldenRingSaveReader?.Configure(currentState.EldenRingSave);
                    blackMythWukongSaveReader?.Configure(currentState.BlackMythWukongSave);
                    liesOfPSaveReader?.Configure(currentState.LiesOfPSave);
                    return (Ticket: runtimePublication.BeginRead(currentState), currentState.SelectedGameId);
                });
                RuntimeGameReadResult? result = await runtimeReaders!
                    .PollAsync(read.SelectedGameId, cancellationToken)
                    .ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (mainWindowCloseRequested) return;
                    PersistentTrackerState currentState = viewModel.CurrentState!;
                    runtimePublication.CompleteRead(read.Ticket, currentState, result,
                        viewModel.ApplyRuntimeReaderResult, publication =>
                        {
                            textExportPublisher?.PublishRuntimeObservation(runtimePublication.CurrentState!, publication);
                            hostedConnection?.PublishAccepted(runtimePublication.CurrentState!, publication);
                        }, cancellationToken);
                });
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// A named mutex may only be released by the thread that acquired it. Startup
    /// acquires the single-instance lease on the WPF dispatcher, while awaited
    /// shutdown work resumes on worker threads. Marshal just the final lease
    /// release back to that owning dispatcher after all components have stopped.
    /// </summary>
    private sealed class DispatcherBoundDisposable(Dispatcher dispatcher, IDisposable inner) : IDisposable
    {
        private readonly Dispatcher dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        private readonly IDisposable inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public void Dispose()
        {
            if (dispatcher.CheckAccess())
            {
                inner.Dispose();
                return;
            }

            dispatcher.Invoke(inner.Dispose);
        }
    }
}
