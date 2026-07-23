using SoulsTracker.Desktop;

namespace SoulsTracker.Desktop.Tests;

public sealed class EldenRingProfileSlotChoiceTests
{
    [Fact]
    public void LabelPrefersFullyValidatedNameAndLevel()
    {
        Assert.Equal("Kairo \u00B7 Level 125", new EldenRingProfileSlotChoice(0, Name: "Kairo", Level: 125).Label);
    }

    [Fact]
    public void LabelUsesSafePartialFallbacksAndClearlyMarksEmptySlots()
    {
        Assert.Equal("Kairo", new EldenRingProfileSlotChoice(0, Name: "Kairo").Label);
        Assert.Equal("Character 2 \u00B7 Level 50", new EldenRingProfileSlotChoice(1, Level: 50).Label);
        Assert.Equal("Character 3", new EldenRingProfileSlotChoice(2).Label);
        Assert.Equal("Character 4 \u00B7 Empty", new EldenRingProfileSlotChoice(3, IsEmpty: true).Label);
    }
}
