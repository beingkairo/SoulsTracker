using System.Windows.Input;
using SoulsTracker.Domain;

namespace SoulsTracker.Desktop;

internal sealed record GlobalHotkeyBinding(uint Modifiers, uint VirtualKey, string DisplayText)
{
    internal const uint ControlAltModifier = 0x0003;
    internal const uint ShiftModifier = 0x0004;
    internal const uint NoRepeatModifier = 0x4000;

    internal static GlobalHotkeyBinding IncrementDefault { get; } = new(ControlAltModifier | ShiftModifier, 0x26, "Ctrl+Alt+Shift+Up Arrow");
    internal static GlobalHotkeyBinding DecrementDefault { get; } = new(ControlAltModifier | ShiftModifier, 0x28, "Ctrl+Alt+Shift+Down Arrow");

    internal uint NativeModifiers => Modifiers | NoRepeatModifier;

    internal static bool TryCreate(Key key, ModifierKeys modifiers, out GlobalHotkeyBinding? binding, out string message)
    {
        binding = null;
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            message = "Windows-key bindings are reserved. Choose a non-reserved key or chord.";
            return false;
        }

        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            message = "Choose one supported non-modifier key.";
            return false;
        }

        int value = KeyInterop.VirtualKeyFromKey(key);
        if (value <= 0 || !ManualBloodborneHotkeyConfiguration.IsSupportedVirtualKey((uint)value))
        {
            message = "Choose one supported non-modifier key.";
            return false;
        }

        uint nativeModifiers = (modifiers.HasFlag(ModifierKeys.Control) ? 0x0002u : 0u) |
            (modifiers.HasFlag(ModifierKeys.Alt) ? 0x0001u : 0u) |
            (modifiers.HasFlag(ModifierKeys.Shift) ? ShiftModifier : 0u);
        binding = new GlobalHotkeyBinding(nativeModifiers, (uint)value, Describe(key, modifiers));
        message = string.Empty;
        return true;
    }

    internal static bool TryFromPersisted(uint modifiers, uint virtualKey, out GlobalHotkeyBinding? binding)
    {
        binding = null;
        if (modifiers > (ControlAltModifier | ShiftModifier) ||
            !ManualBloodborneHotkeyConfiguration.IsSupportedVirtualKey(virtualKey))
        {
            return false;
        }
        Key key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        ModifierKeys wpfModifiers = ((modifiers & 0x0002) != 0 ? ModifierKeys.Control : ModifierKeys.None) |
            ((modifiers & 0x0001) != 0 ? ModifierKeys.Alt : ModifierKeys.None) |
            ((modifiers & ShiftModifier) != 0 ? ModifierKeys.Shift : ModifierKeys.None);
        binding = new GlobalHotkeyBinding(modifiers, virtualKey, Describe(key, wpfModifiers));
        return true;
    }

    internal static string Describe(Key key, ModifierKeys modifiers) =>
        $"{(modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : string.Empty)}{(modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : string.Empty)}{(modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : string.Empty)}{(key switch { Key.Up => "Up Arrow", Key.Down => "Down Arrow", _ => key.ToString() })}";
}

internal sealed record GlobalHotkeySettings(GlobalHotkeyBinding Increment, GlobalHotkeyBinding Decrement)
{
    internal static GlobalHotkeySettings Default { get; } = new(GlobalHotkeyBinding.IncrementDefault, GlobalHotkeyBinding.DecrementDefault);
}
