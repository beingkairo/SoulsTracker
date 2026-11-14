using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Net.Http;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using SoulsTracker.Application;
using SoulsTracker.Domain;
using SoulsTracker.Infrastructure;

namespace SoulsTracker.Desktop;

public sealed partial class DesktopTrackerViewModel
{


    private string? globalHotkeyStatus;
    private GlobalHotkeySettings hotkeySettings = GlobalHotkeySettings.Default;
    private string pendingIncrementHotkey = GlobalHotkeyBinding.IncrementDefault.DisplayText;
    private string pendingDecrementHotkey = GlobalHotkeyBinding.DecrementDefault.DisplayText;
    private GlobalHotkeyBinding pendingIncrementBinding = GlobalHotkeyBinding.IncrementDefault;
    private GlobalHotkeyBinding pendingDecrementBinding = GlobalHotkeyBinding.DecrementDefault;
    private bool isHotkeyRecording;
    private bool recordingIncrementHotkey;
    private GlobalHotkeyBinding? hotkeyBindingBeforeRecording;
    private Func<GlobalHotkeySettings, Task<GlobalHotkeyRegistrationResult>>? applyHotkeysAsync;
    private long hotkeyApplyVersion;
    private long hotkeyCommittedVersion;

    public string? GlobalHotkeyStatus { get => globalHotkeyStatus; private set => SetField(ref globalHotkeyStatus, value); }
    public string PendingIncrementHotkey { get => pendingIncrementHotkey; set => SetField(ref pendingIncrementHotkey, value); }
    public string PendingDecrementHotkey { get => pendingDecrementHotkey; set => SetField(ref pendingDecrementHotkey, value); }
    /// <summary>True while the in-app manual-hotkey capture surface owns keyboard input.</summary>
    public bool IsHotkeyRecording { get => isHotkeyRecording; private set => SetField(ref isHotkeyRecording, value); }
    /// <summary>True while the Elden Ring acknowledgement gate is visible.</summary>
    public string ActiveIncrementHotkey => hotkeySettings.Increment.DisplayText;
    public string ActiveDecrementHotkey => hotkeySettings.Decrement.DisplayText;

    internal void SetGlobalHotkeyStatus(string status) => GlobalHotkeyStatus = string.IsNullOrWhiteSpace(status)
        ? throw new ArgumentException("A global hotkey status is required.", nameof(status))
        : status;

    internal void SetGlobalHotkeySettings(GlobalHotkeySettings settings)
    {
        hotkeySettings = settings ?? throw new ArgumentNullException(nameof(settings));
        PendingIncrementHotkey = settings.Increment.DisplayText;
        PendingDecrementHotkey = settings.Decrement.DisplayText;
        pendingIncrementBinding = settings.Increment;
        pendingDecrementBinding = settings.Decrement;
        OnPropertyChanged(nameof(ActiveIncrementHotkey));
        OnPropertyChanged(nameof(ActiveDecrementHotkey));
    }

    internal void ConfigureGlobalHotkeys(GlobalHotkeySettings settings, Func<GlobalHotkeySettings, Task<GlobalHotkeyRegistrationResult>> apply)
    {
        hotkeyCommittedVersion = ++hotkeyApplyVersion;
        applyHotkeysAsync = apply ?? throw new ArgumentNullException(nameof(apply));
        SetGlobalHotkeySettings(settings);
    }

    public void CapturePendingHotkey(bool increment, System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!GlobalHotkeyBinding.TryCreate(key, modifiers, out GlobalHotkeyBinding? binding, out string message))
        {
            SetGlobalHotkeyStatus(message);
            return;
        }
        GlobalHotkeyBinding captured = binding!;
        if (increment) { pendingIncrementBinding = captured; PendingIncrementHotkey = captured.DisplayText; } else { pendingDecrementBinding = captured; PendingDecrementHotkey = captured.DisplayText; }
        SetGlobalHotkeyStatus("Choose the other binding or apply the change.");
    }

    /// <summary>Starts an in-app recording session without changing an active global binding.</summary>
    public void BeginHotkeyRecording(bool increment)
    {
        if (!IsGlobalHotkeyConfigurationAvailable || !ControlsEnabled) return;

        recordingIncrementHotkey = increment;
        hotkeyBindingBeforeRecording = increment ? pendingIncrementBinding : pendingDecrementBinding;
        IsHotkeyRecording = true;
    }

    /// <summary>Captures the next valid binding for the active recording session.</summary>
    public void CaptureRecordedHotkey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        if (!IsHotkeyRecording) return;
        CapturePendingHotkey(recordingIncrementHotkey, key, modifiers);
    }

    /// <summary>Cancels a recording session and restores the pending value it started with.</summary>
    public void CancelHotkeyRecording()
    {
        if (!IsHotkeyRecording) return;

        if (hotkeyBindingBeforeRecording is GlobalHotkeyBinding original)
        {
            if (recordingIncrementHotkey)
            {
                pendingIncrementBinding = original;
                PendingIncrementHotkey = original.DisplayText;
            }
            else
            {
                pendingDecrementBinding = original;
                PendingDecrementHotkey = original.DisplayText;
            }
        }

        hotkeyBindingBeforeRecording = null;
        IsHotkeyRecording = false;
    }

    /// <summary>Applies a recording session's captured binding and always releases the capture surface.</summary>
    public async Task<bool> SaveRecordedHotkeyAsync()
    {
        if (!IsHotkeyRecording) return false;

        try
        {
            return await ApplyGlobalHotkeysAsync();
        }
        finally
        {
            hotkeyBindingBeforeRecording = null;
            IsHotkeyRecording = false;
        }
    }

    public async Task<bool> ApplyGlobalHotkeysAsync()
    {
        long version = ++hotkeyApplyVersion;
        if (applyHotkeysAsync is null) { SetGlobalHotkeyStatus("Global hotkeys are unavailable. The desktop controls remain available."); return false; }
        if (!TryParsePending(out GlobalHotkeySettings? candidate, out string message)) { SetGlobalHotkeyStatus(message); return false; }
        GlobalHotkeyRegistrationResult result = await applyHotkeysAsync(candidate!);
        // Superseded notifications must not discard an already committed binding.
        if (result.IsRegistered && version > hotkeyCommittedVersion)
        {
            hotkeyCommittedVersion = version;
            SetGlobalHotkeySettings(candidate!);
        }
        if (version != hotkeyApplyVersion) return false;
        SetGlobalHotkeyStatus(result.StatusMessage);
        if (!result.IsRegistered) SetGlobalHotkeySettings(hotkeySettings);
        return result.IsRegistered;
    }

    private bool TryParsePending(out GlobalHotkeySettings? settings, out string message)
    {
        settings = null;
        if (pendingIncrementBinding == pendingDecrementBinding) { message = "Increment and decrement cannot use the same binding."; return false; }
        settings = new GlobalHotkeySettings(pendingIncrementBinding, pendingDecrementBinding); message = string.Empty; return true;
    }
}
