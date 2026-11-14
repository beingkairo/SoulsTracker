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

    private bool appearanceResetConfirmationOpen;

    private void AppearanceRow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var row = (Grid)sender;
        if (row.Children.Count != 2) return;
        bool narrow = e.NewSize.Width < 350;
        if (row.RowDefinitions.Count == 0)
        {
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var label = (FrameworkElement)row.Children[0];
        var field = (FrameworkElement)row.Children[1];
        Grid.SetColumnSpan(label, narrow ? 2 : 1);
        Grid.SetRow(field, narrow ? 1 : 0);
        Grid.SetColumn(field, narrow ? 0 : 1);
        Grid.SetColumnSpan(field, narrow ? 2 : 1);
        label.Margin = narrow ? new Thickness(0, 0, 0, 4) : new Thickness(0);
    }


    private async void ApplyTotalDeathsAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            AppearanceApplyStatus.Text = "";
            await viewModel.ApplyOverlayAppearanceAsync(totalDeaths: true);
            if (!copyFeedbackClosed) UpdateAppearanceFeedback();
        }
    }

    private void UpdateAppearanceFeedback()
    {
        if (copyFeedbackClosed || DataContext is not DesktopTrackerViewModel vm) return;
        string? message = vm.TotalDeathsAppearanceStatus;
        if (string.IsNullOrEmpty(message) && !vm.IsAppearanceApplySuccessful) return;
        AppearanceApplyStatus.Tag = message;
        System.Windows.Automation.AutomationProperties.SetHelpText(AppearanceApplyStatus, message ?? "");
        AppearanceApplyStatus.Text = message is { Length: > 80 } ? "Correct the highlighted appearance fields, then Apply." : message ?? "";
    }
    private async void ResetSelectedOverlayAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (appearanceResetConfirmationOpen || DataContext is not DesktopTrackerViewModel { PresentationControlsEnabled: true } viewModel) return;
        appearanceResetConfirmationOpen = true;
        try
        {
            var dialog = new AppearanceResetDialog { Owner = this, Resources = Resources };
            if (dialog.ShowDialog() == true) await viewModel.ResetOverlayAppearanceAsync(totalDeaths: true);
        }
        finally
        {
            appearanceResetConfirmationOpen = false;
            if (!copyFeedbackClosed) AppearanceActions.Focus();
        }
    }
}
