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

    private string? ChooseSaveDirectory(string title, string? currentDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (System.IO.Directory.Exists(currentDirectory)) dialog.InitialDirectory = currentDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private async void BrowseEldenRingSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel &&
            chooseSaveDirectory("Choose Elden Ring save directory", viewModel.EldenRingDirectoryPath) is { } directory)
        {
            await viewModel.SetEldenRingSaveDirectoryAsync(directory);
        }
    }

    private async void RescanEldenRingSaves_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.RescanEldenRingSavesAsync();
    }

    private void ChangeEldenRingSave_Click(object sender, RoutedEventArgs e)
    {
        BrowseEldenRingSave_Click(sender, e);
    }

    private void CancelEldenRingChange_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.CancelEldenRingChange();
    }

    private async void EldenRingSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringEldenRingSaveSelection) return;
        if (sender is System.Windows.Controls.ComboBox selector
            && e.AddedItems.OfType<DiscoveredLocalSave>().FirstOrDefault() is { } choice
            && DataContext is DesktopTrackerViewModel viewModel)
        {
            if (ReferenceEquals(choice, viewModel.SelectedEldenRingSaveChoice)) return;
            await viewModel.SelectEldenRingSaveChoiceAsync(choice);
            DiscoveredLocalSave? committedChoice = viewModel.SelectedEldenRingSaveChoice;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(selector.SelectedItem, committedChoice)) return;
                try
                {
                    isRestoringEldenRingSaveSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!ReferenceEquals(selector.SelectedItem, committedChoice))
                    {
                        selector.SetCurrentValue(System.Windows.Controls.ComboBox.SelectedItemProperty, committedChoice);
                    }
                }
                finally
                {
                    isRestoringEldenRingSaveSelection = false;
                }
            });
        }
    }

    private async void BrowseBlackMythWukongSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel &&
            chooseSaveDirectory("Choose Black Myth: Wukong save directory", viewModel.BlackMythWukongDirectoryPath) is { } directory)
        {
            await viewModel.SetBlackMythWukongSaveDirectoryAsync(directory);
        }
    }

    private async void RescanBlackMythWukongSaves_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.RescanBlackMythWukongSavesAsync();
    }

    private void ChangeBlackMythWukongSave_Click(object sender, RoutedEventArgs e)
    {
        BrowseBlackMythWukongSave_Click(sender, e);
    }

    private void CancelBlackMythWukongChange_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.CancelBlackMythWukongChange();
    }

    private async void BlackMythWukongSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringBlackMythWukongSaveSelection) return;
        if (sender is System.Windows.Controls.ComboBox selector
            && e.AddedItems.OfType<DiscoveredLocalSave>().FirstOrDefault() is { } choice
            && DataContext is DesktopTrackerViewModel viewModel)
        {
            if (ReferenceEquals(choice, viewModel.SelectedBlackMythWukongSaveChoice)) return;
            await viewModel.SelectBlackMythWukongSaveChoiceAsync(choice);
            DiscoveredLocalSave? committedChoice = viewModel.SelectedBlackMythWukongSaveChoice;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(selector.SelectedItem, committedChoice)) return;
                try
                {
                    isRestoringBlackMythWukongSaveSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!ReferenceEquals(selector.SelectedItem, committedChoice))
                    {
                        selector.SetCurrentValue(System.Windows.Controls.ComboBox.SelectedItemProperty, committedChoice);
                    }
                }
                finally
                {
                    isRestoringBlackMythWukongSaveSelection = false;
                }
            });
        }
    }

    private async void BrowseLiesOfPSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel &&
            chooseSaveDirectory("Choose Lies of P save directory", viewModel.LiesOfPDirectoryPath) is { } directory)
        {
            await viewModel.SetLiesOfPSaveDirectoryAsync(directory);
        }
    }

    private async void RescanLiesOfPSaves_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) await viewModel.RescanLiesOfPSavesAsync();
    }

    private void ChangeLiesOfPSave_Click(object sender, RoutedEventArgs e)
    {
        BrowseLiesOfPSave_Click(sender, e);
    }

    private void CancelLiesOfPChange_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopTrackerViewModel viewModel) viewModel.CancelLiesOfPChange();
    }

    private async void LiesOfPSave_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringLiesOfPSaveSelection) return;
        if (sender is System.Windows.Controls.ComboBox selector &&
            e.AddedItems.OfType<DiscoveredLocalSave>().FirstOrDefault() is { } choice &&
            DataContext is DesktopTrackerViewModel viewModel)
        {
            if (ReferenceEquals(choice, viewModel.SelectedLiesOfPSaveChoice)) return;
            await viewModel.SelectLiesOfPSaveChoiceAsync(choice);
            DiscoveredLocalSave? committedChoice = viewModel.SelectedLiesOfPSaveChoice;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    isRestoringLiesOfPSaveSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!ReferenceEquals(selector.SelectedItem, committedChoice)) selector.SelectedItem = committedChoice;
                }
                finally
                {
                    isRestoringLiesOfPSaveSelection = false;
                }
            });
        }
    }

    private async void EldenRingProfileSlot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRestoringEldenRingProfileSelection) return;
        if (DataContext is DesktopTrackerViewModel viewModel
            && sender is System.Windows.Controls.ComboBox { SelectedItem: EldenRingProfileSlotChoice slot } selector
            && viewModel.SelectedEldenRingProfileSlot != slot)
        {
            await viewModel.SetEldenRingProfileSlotAsync(slot);
            EldenRingProfileSlotChoice? committedSlot = viewModel.SelectedEldenRingProfileSlot;
            if (selector.Dispatcher.HasShutdownStarted) return;
            await selector.Dispatcher.InvokeAsync(() =>
            {
                if (Equals(selector.SelectedItem, committedSlot)) return;
                try
                {
                    isRestoringEldenRingProfileSelection = true;
                    BindingOperations.GetBindingExpression(selector, System.Windows.Controls.ComboBox.SelectedItemProperty)?.UpdateTarget();
                    if (!Equals(selector.SelectedItem, committedSlot))
                    {
                        selector.SetCurrentValue(System.Windows.Controls.ComboBox.SelectedItemProperty, committedSlot);
                    }
                }
                finally
                {
                    isRestoringEldenRingProfileSelection = false;
                }
            });
        }
    }
}
