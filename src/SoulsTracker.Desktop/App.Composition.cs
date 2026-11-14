using System.ComponentModel;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public partial class App
{
    private SerializedTrackerCoordinator? coordinator;
    private DesktopTrackerViewModel? trackerViewModel;
    private TextExportStatePublisher? textExportPublisher;
    private HostedOverlayConnection? hostedConnection;

    private async Task StartTrackerCoreAsync()
    {
        DesktopDataRootSelection stateSelection = dataRootSelection ?? throw new InvalidOperationException("The desktop data root was not initialized.");
        var protector = new CurrentUserDpapiSecretProtector();
        var repository = new SqliteTrackerStateRepository(stateSelection.RootPath, "tracker.db");
        var preference = new HostedOverlayPreferenceStore(stateSelection.RootPath);
        var pairing = new HostedPublisherConfigurationStore(Path.Combine(stateSelection.RootPath, "hosted-pairing.private"),
            protector, HostedProductionOrigins.Approved);
        bool hostedEnabled;
        try
        {
            hostedEnabled = await preference.ResolveAsync(async ct =>
            {
                try { return await pairing.LoadAsync(ct) is not null; }
                catch { return false; }
            }, startupCancellation.Token);
        }
        catch { hostedEnabled = false; /* Local tracking must not depend on preference storage. */ }
        hostedConnection = new HostedOverlayConnection(Dispatcher,
            pairing,
            pendingStore: new HostedProvisioningStateStore(Path.Combine(stateSelection.RootPath, "overlay-setup.private"),
                protector, HostedProductionOrigins.Approved),
            preferenceStore: preference, initiallyEnabled: hostedEnabled,
            createProvisioningClient: () => new HostedOverlayProvisioningClient(HostedProductionOrigins.Approved.Single()));
        textExportPublisher = new TextExportStatePublisher();
        coordinator = new SerializedTrackerCoordinator(repository,
            new DesktopStateChangePublisher(Dispatcher, runtimePublication,
                new CompositeTrackerStateChangePublisher(hostedConnection, textExportPublisher)),
            new SqliteConfirmedLegacyImportCommitter(repository));

        var viewModel = new DesktopTrackerViewModel(coordinator);
        trackerViewModel = viewModel;
        var trayPreference = new TrayClosePreferenceStore(stateSelection.RootPath);
        try
        {
            bool minimizeOnClose = await trayPreference.ResolveAsync(startupCancellation.Token);
            viewModel.ConfigureTrayClosePreference(minimizeOnClose, enabled => trayPreference.SaveAsync(enabled));
        }
        catch { /* Fail closed to normal shutdown if preference storage is unavailable. */ }
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
        if (pendingRestore) RestoreMainWindow();
        trayLifecycle = new DesktopTrayLifecycle(() => { window.RememberTrayFocus(); window.Hide(); }, RestoreMainWindow,
            () => _ = ExitApplicationAsync(), message =>
            {
                if (!window.IsVisible)
                {
                    try { trayIcon?.ShowGuidance(message); }
                    catch { TrayFailed(window); }
                }
            }, () => viewModel.MinimizeToTrayOnClose);
        window.StateChanged += (_, _) =>
        {
            if (window.WindowState == WindowState.Minimized) trayLifecycle.Minimized();
        };
        try
        {
            trayIcon = new WindowsTrayIcon(window, trayLifecycle.Restore, trayLifecycle.Exit,
                () => trayLifecycle.TrayAvailable = true,
                () => TrayFailed(window));
        }
        catch { TrayFailed(window); }
        await viewModel.InitializeAsync(startupCancellation.Token);
        if (!mainWindowCloseRequested) await viewModel.DiscoverHollowKnightAsync();
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
            hollowKnightReader = new HollowKnightRecordReader();
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
                            "0045931B8914504531B7864A9488D396DC50CBAF524964016E1D69C3D1173131"),
                            new ProcessModuleFileIdentity(
                                "DarkSoulsII.exe",
                                "1,0,3,0",
                                "1,0,3,0",
                                "3095FC38140C267A5BC7289FD17C5DA5CBF5DBBDD3667116242F905C3509EFC3"))),
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
                    new BloodborneRuntimeDeathReader(),
                    eldenRingSaveReader,
                    blackMythWukongSaveReader,
                    liesOfPSaveReader,
                    hollowKnightReader,
                ]);
            runtimeReaderCancellation = new CancellationTokenSource();
            runtimeReaderPollingTask = PollRuntimeReadersAsync(viewModel, runtimeReaderCancellation.Token);
        }
        if (!mainWindowCloseRequested) viewModel.StartStartupUpdateCheck();
    }
}
