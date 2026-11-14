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
    private DesktopGlobalHotkeyService? globalHotkeys;

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
        activationPipe?.Dispose();
        activationPipe = null;
        trayIcon?.Dispose();
        trayIcon = null;
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

}
