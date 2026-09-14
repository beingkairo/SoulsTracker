using SoulsTracker.Domain;

namespace SoulsTracker.Domain.Tests;

public sealed class EldenRingMissedDeathAdjustmentsTests
{
    [Fact]
    public void AdjustmentIsCaseInsensitivePerSaveAndIsolatedPerCharacter()
    {
        var first = new EldenRingSaveConfiguration("C:\\Saves\\ER0000.sl2", 0);
        var same = new EldenRingSaveConfiguration("c:\\saves\\er0000.sl2", 0);
        var second = new EldenRingSaveConfiguration("C:\\Saves\\ER0000.sl2", 1);
        EldenRingMissedDeathAdjustments adjustments = EldenRingMissedDeathAdjustments.Empty.Increment(first).Increment(first);
        Assert.Equal(2, adjustments.Get(same));
        Assert.Equal(0, adjustments.Get(second));
    }
}
