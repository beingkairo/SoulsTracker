namespace SoulsTracker.Domain;

/// <summary>Validated, local-only bindings shared by supported manual adjustments.</summary>
public sealed record GlobalHotkeyConfiguration(uint IncrementModifiers, uint IncrementVirtualKey, uint DecrementModifiers, uint DecrementVirtualKey)
{
    public static GlobalHotkeyConfiguration Default { get; } = new(0x0007, 0x26, 0x0007, 0x28);

    public bool IsValid => IsBindingValid(IncrementModifiers, IncrementVirtualKey) &&
                           IsBindingValid(DecrementModifiers, DecrementVirtualKey) &&
                           (IncrementModifiers != DecrementModifiers || IncrementVirtualKey != DecrementVirtualKey);

    public static bool IsSupportedVirtualKey(uint virtualKey) =>
        virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7B or >= 0x21 and <= 0x28 or 0x20;

    private static bool IsBindingValid(uint modifiers, uint virtualKey) =>
        modifiers <= 0x0007 &&
        IsSupportedVirtualKey(virtualKey);
}
