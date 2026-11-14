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

    private async void GameSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        GameChoice? choice = e.AddedItems.OfType<GameChoice>().LastOrDefault();
        if (DataContext is DesktopTrackerViewModel viewModel && choice is not null)
        {
            // One-way binding also raises SelectionChanged when committed state
            // reapplies the current choice. That is presentation sync, not a
            // new user request; ignoring it prevents an initial no-op request
            // from racing a subsequent user selection.
            if (ReferenceEquals(choice, viewModel.SelectedGame))
            {
                await viewModel.DiscoverHollowKnightAsync();
                return;
            }

            if (viewModel.RequestGameSelection(choice))
            {
                await SelectGameOnWindowDispatcherAsync(viewModel, choice);
                await viewModel.DiscoverHollowKnightAsync();
            }
            else
            {
                GameSelector.SelectedItem = viewModel.SelectedGame;
            }
        }
    }

    private async Task SelectGameOnWindowDispatcherAsync(DesktopTrackerViewModel viewModel, GameChoice choice)
        => await RunOnWindowDispatcherAsync(() => viewModel.SelectGameAsync(choice));

    private async Task RunOnWindowDispatcherAsync(Func<Task> operation)
    {
        SynchronizationContext? priorContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
            await operation();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(priorContext);
        }
    }

    private async void ConfirmEldenRingNotice_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmEldenRingNoticeAsync();
    }

    private void CancelEldenRingNotice_Click(object sender, RoutedEventArgs e)
    {
        CancelEldenRingNotice();
    }

    private void GameSelector_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ShouldSuppressClosedGameSelectorWheel(GameSelector.IsDropDownOpen)) e.Handled = true;
    }

    internal static bool ShouldSuppressClosedGameSelectorWheel(bool isDropDownOpen) => !isDropDownOpen;

    internal async Task<bool> HandleEldenRingNoticeKeyAsync(Key key)
    {
        if (DataContext is not DesktopTrackerViewModel { IsEldenRingNoticeVisible: true }) return false;

        if (key == Key.Escape)
        {
            CancelEldenRingNotice();
            return true;
        }

        if (key == Key.Enter)
        {
            await ConfirmEldenRingNoticeAsync();
            return true;
        }

        return false;
    }

    private async Task ConfirmEldenRingNoticeAsync()
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            await viewModel.ConfirmEldenRingNoticeAsync();
            void ApplyConfirmedSelection()
            {
                GameSelector.SelectedItem = viewModel.SelectedGame;
                RestoreGameSelectorFocus();
            }

            if (Dispatcher.CheckAccess()) ApplyConfirmedSelection();
            else _ = Dispatcher.BeginInvoke(ApplyConfirmedSelection);
        }
    }

    private void CancelEldenRingNotice()
    {
        if (DataContext is DesktopTrackerViewModel viewModel)
        {
            viewModel.CancelEldenRingNotice();
            GameSelector.SelectedItem = viewModel.SelectedGame;
            RestoreGameSelectorFocus();
        }
    }

    private void FocusEldenRingNoticePrimaryAction()
    {
        if (DataContext is not DesktopTrackerViewModel { IsEldenRingNoticeVisible: true }) return;
        FocusManager.SetFocusedElement(EldenRingNoticeOverlay, EldenRingNoticeDialog);
        Keyboard.Focus(EldenRingNoticeDialog);
    }

    private void RestoreGameSelectorFocus()
    {
        if (!IsLoaded || DataContext is DesktopTrackerViewModel { IsEldenRingNoticeVisible: true }) return;
        Keyboard.Focus(GameSelector);
    }
}
