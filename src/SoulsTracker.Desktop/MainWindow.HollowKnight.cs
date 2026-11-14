using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Navigation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using Microsoft.Win32;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

/// <summary>Hosts the P3-01 manual tracking surface.</summary>
public partial class MainWindow : Window
{

    internal static string BuildHollowKnightInstallConfirmation(HollowKnightInstallPlan plan) =>
        $"Install death tracking in {plan.Directory}?\n\nThis will write {plan.Files.Count} files to Hollow Knight.\n\nOther installed mods will remain.\n\nClose Hollow Knight before continuing.";

    private async void BrowseHollowKnight_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel vm && chooseSaveDirectory("Choose Hollow Knight game folder", vm.HollowKnightGameDirectory) is { } directory)
            await vm.SetHollowKnightGameDirectoryAsync(directory);
    }

    private async void InstallHollowKnight_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DesktopTrackerViewModel vm) return;
        if (vm.HasHollowKnightPendingRollback)
        {
            if (System.Windows.MessageBox.Show(this, "Restore the interrupted installation from its verified backups before retrying?",
                "Install Death Tracking", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await vm.RollbackHollowKnightAsync();
            if (vm.ErrorMessage is not null) return;
        }
        var plan = HollowKnightInstallPlanProvider is { } planProvider
            ? await planProvider(vm)
            : await vm.PrepareHollowKnightInstallAsync();
        if (plan is null) return;
        string confirmation = BuildHollowKnightInstallConfirmation(plan);
        MessageBoxResult response = HollowKnightInstallConfirmation?.Invoke(confirmation) ??
            System.Windows.MessageBox.Show(this, confirmation, "Install Death Tracking", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (response == MessageBoxResult.Yes)
            await vm.ApplyHollowKnightInstallAsync(plan);
    }

    private async void UninstallHollowKnight_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel vm && System.Windows.MessageBox.Show(this,
            "Remove only Soulstracker death tracking? Saved totals, the shared Modding API and other mods will remain.",
            "Uninstall Death Tracking", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await vm.UninstallHollowKnightAsync();
    }

    private async void RefreshHollowKnight_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel vm) await vm.DiscoverHollowKnightAsync();
    }
    private async void IncrementHollowKnightDeaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel vm) await vm.IncrementHollowKnightDeathsAsync();
    }
    private async void DecrementHollowKnightDeaths_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel vm) await vm.DecrementHollowKnightDeathsAsync();
    }
}
