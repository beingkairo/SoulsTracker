using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoulsTracker.Desktop;

/// <summary>Owns the two approved native global hotkeys and dispatches them through the desktop view model.</summary>
internal sealed class GlobalHotkeyController : IDisposable
{
    internal const int WindowsHotkeyMessage = 0x0312;
    internal const int IncrementHotkeyId = 0x5001;
    internal const int DecrementHotkeyId = 0x5002;
    internal const uint ControlAltShiftNoRepeatModifier = 0x4007;
    internal const uint UpArrowVirtualKey = 0x26;
    internal const uint DownArrowVirtualKey = 0x28;

    private readonly IWindowsGlobalHotkeyNative native;
    private readonly nint windowHandle;
    private readonly DesktopTrackerViewModel viewModel;
    private bool decrementRegistered;
    private bool disposed;
    private bool incrementRegistered;
    private GlobalHotkeyRegistrationResult? registrationResult;
    private GlobalHotkeySettings activeSettings = GlobalHotkeySettings.Default;

    public GlobalHotkeyController(
        nint windowHandle,
        IWindowsGlobalHotkeyNative native,
        DesktopTrackerViewModel viewModel)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A window handle is required for global hotkey registration.", nameof(windowHandle));
        }

        this.windowHandle = windowHandle;
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public bool IsRegistered => incrementRegistered && decrementRegistered;
    public GlobalHotkeySettings ActiveSettings => activeSettings;

    /// <summary>Registers the required pair atomically, removing the first key if the second is unavailable.</summary>
    public GlobalHotkeyRegistrationResult Register(GlobalHotkeySettings? settings = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (registrationResult is not null)
        {
            return registrationResult;
        }

        settings ??= GlobalHotkeySettings.Default;
        try
        {
            if (!native.RegisterHotKey(
                    windowHandle,
                    IncrementHotkeyId,
                    settings.Increment.NativeModifiers,
                    settings.Increment.VirtualKey))
            {
                return CompleteRegistration(GlobalHotkeyRegistrationResult.Unavailable);
            }

            incrementRegistered = true;
            if (!native.RegisterHotKey(
                    windowHandle,
                    DecrementHotkeyId,
                    settings.Decrement.NativeModifiers,
                    settings.Decrement.VirtualKey))
            {
                UnregisterAcquiredHotkeys();
                return CompleteRegistration(GlobalHotkeyRegistrationResult.Unavailable);
            }

            decrementRegistered = true;
            activeSettings = settings;
            return CompleteRegistration(GlobalHotkeyRegistrationResult.Registered);
        }
        catch (Exception)
        {
            UnregisterAcquiredHotkeys();
            return CompleteRegistration(GlobalHotkeyRegistrationResult.Unavailable);
        }
    }

    public GlobalHotkeyRegistrationResult Replace(GlobalHotkeySettings candidate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Increment == candidate.Decrement)
        {
            return GlobalHotkeyRegistrationResult.Invalid("Increment and decrement cannot use the same binding.");
        }

        GlobalHotkeySettings previous = activeSettings;
        UnregisterAcquiredHotkeys();
        registrationResult = null;
        GlobalHotkeyRegistrationResult result = Register(candidate);
        if (result.IsRegistered)
        {
            return result;
        }

        registrationResult = null;
        GlobalHotkeyRegistrationResult restored = Register(previous);
        return restored.IsRegistered
            ? GlobalHotkeyRegistrationResult.ChangeNotAppliedPreviousActive
            : GlobalHotkeyRegistrationResult.UnavailableWithoutRestore;
    }

    /// <summary>Maps a native hotkey message to the existing guarded view-model command methods.</summary>
    public Task<bool> HandleMessageAsync(int message, nint wParam)
    {
        if (!IsRegistered || message != WindowsHotkeyMessage)
        {
            return Task.FromResult(false);
        }

        return unchecked((int)wParam.ToInt64()) switch
        {
            IncrementHotkeyId => IncrementAsync(),
            DecrementHotkeyId => DecrementAsync(),
            _ => Task.FromResult(false),
        };
    }

    /// <summary>Starts command handling without blocking the native window procedure.</summary>
    public bool TryDispatchWindowMessage(int message, nint wParam)
    {
        if (!CanHandleMessage(message, wParam))
        {
            return false;
        }

        _ = HandleMessageAsync(message, wParam);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        UnregisterAcquiredHotkeys();
        GC.SuppressFinalize(this);
    }

    private async Task<bool> IncrementAsync()
    {
        await viewModel.IncrementManualDeathsAsync();
        return true;
    }

    private async Task<bool> DecrementAsync()
    {
        await viewModel.DecrementManualDeathsAsync();
        return true;
    }

    private bool CanHandleMessage(int message, nint wParam) =>
        IsRegistered &&
        message == WindowsHotkeyMessage &&
        unchecked((int)wParam.ToInt64()) is IncrementHotkeyId or DecrementHotkeyId;

    private GlobalHotkeyRegistrationResult CompleteRegistration(GlobalHotkeyRegistrationResult result)
    {
        registrationResult = result;
        viewModel.SetGlobalHotkeyStatus(result.StatusMessage);
        return result;
    }

    private void UnregisterAcquiredHotkeys()
    {
        UnregisterHotkey(ref decrementRegistered, DecrementHotkeyId);
        UnregisterHotkey(ref incrementRegistered, IncrementHotkeyId);
    }

    private void UnregisterHotkey(ref bool isRegistered, int hotkeyId)
    {
        if (!isRegistered)
        {
            return;
        }

        isRegistered = false;
        try
        {
            _ = native.UnregisterHotKey(windowHandle, hotkeyId);
        }
        catch (DllNotFoundException)
        {
            // Shutdown continues even when Windows native cleanup is unavailable.
        }
        catch (EntryPointNotFoundException)
        {
            // Shutdown continues even when Windows native cleanup is unavailable.
        }
        catch (SEHException)
        {
            // Shutdown continues even when Windows reports a native cleanup failure.
        }
    }
}

internal enum GlobalHotkeyRegistrationStatus
{
    Registered,
    Unavailable,
}

internal sealed record GlobalHotkeyRegistrationResult(GlobalHotkeyRegistrationStatus Status, string StatusMessage)
{
    public static GlobalHotkeyRegistrationResult Registered { get; } = new(
        GlobalHotkeyRegistrationStatus.Registered,
        "Manual hotkeys are active.");

    public static GlobalHotkeyRegistrationResult Unavailable { get; } = new(
        GlobalHotkeyRegistrationStatus.Unavailable,
        "Global hotkeys are unavailable because another application is using one of the required key combinations. The desktop controls remain available.");

    public static GlobalHotkeyRegistrationResult UnavailableWithoutRestore { get; } = new(
        GlobalHotkeyRegistrationStatus.Unavailable,
        "Global hotkeys are unavailable. The desktop controls remain available.");

    public static GlobalHotkeyRegistrationResult SaveFailed { get; } = new(
        GlobalHotkeyRegistrationStatus.Unavailable,
        "The hotkey change could not be saved. The previous hotkeys remain active.");

    public static GlobalHotkeyRegistrationResult ChangeNotAppliedPreviousActive { get; } = new(
        GlobalHotkeyRegistrationStatus.Unavailable,
        "The hotkey change was not applied. The previous hotkeys remain active.");

    public static GlobalHotkeyRegistrationResult Invalid(string message) => new(GlobalHotkeyRegistrationStatus.Unavailable, message);

    public bool IsRegistered => Status == GlobalHotkeyRegistrationStatus.Registered;
}

/// <summary>Minimal native boundary for the approved RegisterHotKey lifetime.</summary>
internal interface IWindowsGlobalHotkeyNative
{
    bool RegisterHotKey(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey);

    bool UnregisterHotKey(nint windowHandle, int hotkeyId);
}

internal sealed class WindowsGlobalHotkeyNative : IWindowsGlobalHotkeyNative
{
    public bool RegisterHotKey(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey) =>
        RegisterHotKeyNative(windowHandle, hotkeyId, modifiers, virtualKey);

    public bool UnregisterHotKey(nint windowHandle, int hotkeyId) =>
        UnregisterHotKeyNative(windowHandle, hotkeyId);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKeyNative(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKeyNative(nint windowHandle, int hotkeyId);
}

/// <summary>Small desktop seam that supplies a native message target and removes its hook during shutdown.</summary>
internal interface IGlobalHotkeyMessageSink : IDisposable
{
    nint WindowHandle { get; }

    void SetMessageHandler(Func<int, nint, bool> messageHandler);
}

internal sealed class HwndGlobalHotkeyMessageSink : IGlobalHotkeyMessageSink
{
    private Func<int, nint, bool>? messageHandler;
    private HwndSourceHook? windowProcedure;
    private HwndSource? source;

    public HwndGlobalHotkeyMessageSink(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        source = PresentationSource.FromVisual(window) as HwndSource
            ?? throw new InvalidOperationException("The shown main window does not have a native message source.");
    }

    public nint WindowHandle => source?.Handle
        ?? throw new ObjectDisposedException(nameof(HwndGlobalHotkeyMessageSink));

    public void SetMessageHandler(Func<int, nint, bool> messageHandler)
    {
        ObjectDisposedException.ThrowIf(source is null, this);
        ArgumentNullException.ThrowIfNull(messageHandler);

        if (windowProcedure is not null)
        {
            throw new InvalidOperationException("A global hotkey message handler is already attached.");
        }

        this.messageHandler = messageHandler;
        windowProcedure = WindowProcedure;
        source.AddHook(windowProcedure);
    }

    public void Dispose()
    {
        HwndSource? currentSource = source;
        HwndSourceHook? currentProcedure = windowProcedure;
        source = null;
        windowProcedure = null;
        messageHandler = null;

        if (currentSource is not null && currentProcedure is not null)
        {
            currentSource.RemoveHook(currentProcedure);
        }
    }

    private nint WindowProcedure(nint windowHandle, int message, nint wParam, nint lParam, ref bool handled)
    {
        Func<int, nint, bool>? handler = messageHandler;
        if (handler is not null && handler(message, wParam))
        {
            handled = true;
        }

        return nint.Zero;
    }
}

/// <summary>Coordinates message-hook removal and native unregistration as one desktop-owned lifetime.</summary>
internal sealed class DesktopGlobalHotkeyService : IDisposable
{
    private readonly GlobalHotkeyController controller;
    private readonly IGlobalHotkeyMessageSink messageSink;
    private bool disposed;
    private bool messageSinkDisposed;

    public DesktopGlobalHotkeyService(
        IGlobalHotkeyMessageSink messageSink,
        IWindowsGlobalHotkeyNative native,
        DesktopTrackerViewModel viewModel)
    {
        this.messageSink = messageSink ?? throw new ArgumentNullException(nameof(messageSink));
        controller = new GlobalHotkeyController(messageSink.WindowHandle, native, viewModel);
        messageSink.SetMessageHandler(controller.TryDispatchWindowMessage);
    }

    public GlobalHotkeyRegistrationResult Start(GlobalHotkeySettings? settings = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return controller.Register(settings);
    }

    public GlobalHotkeySettings ActiveSettings => controller.ActiveSettings;
    public bool IsRegistered => controller.IsRegistered;

    public GlobalHotkeyRegistrationResult Replace(GlobalHotkeySettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return controller.Replace(settings);
    }

    public Task<bool> HandleMessageAsync(int message, nint wParam) => controller.HandleMessageAsync(message, wParam);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            DisposeMessageSink();
        }
        finally
        {
            controller.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void DisposeMessageSink()
    {
        if (messageSinkDisposed)
        {
            return;
        }

        messageSinkDisposed = true;
        messageSink.Dispose();
    }
}
