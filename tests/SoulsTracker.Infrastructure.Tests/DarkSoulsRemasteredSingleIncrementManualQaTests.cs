using Xunit.Abstractions;

namespace SoulsTracker.Infrastructure.Tests;

/// <summary>
/// This test is inert unless QA supplies the per-run confirmation after the
/// user has confirmed that the offline game is open and will perform one
/// manual death.
/// </summary>
public sealed class DarkSoulsRemasteredSingleIncrementManualQaTests
{
    private readonly ITestOutputHelper output;

    public DarkSoulsRemasteredSingleIncrementManualQaTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [OfflineDarkSoulsRemasteredSingleIncrementFact]
    [Trait("Category", "ManualLiveValidation")]
    public async Task ReportsOnlyWhetherTheFrozenHypothesisObservedOneManualIncrement()
    {
        string? confirmation = Environment.GetEnvironmentVariable(
            DarkSoulsRemasteredSingleIncrementValidationHarness.ConfirmationEnvironmentVariable);
        DarkSoulsRemasteredSingleIncrementValidationResult? result = await
            DarkSoulsRemasteredSingleIncrementValidationHarness.CreateForManualQaRun().RunIfConfirmedAsync(
                confirmation,
                CancellationToken.None);

        Assert.NotNull(result);
        output.WriteLine($"Dark Souls Remastered single-increment outcome: {result.Outcome}");
        Assert.Equal(DarkSoulsRemasteredSingleIncrementValidationOutcome.ObservedSingleIncrement, result.Outcome);
    }
}

/// <summary>
/// The test framework reports this test as skipped unless the explicit,
/// user-coordinated confirmation is present before test discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class OfflineDarkSoulsRemasteredSingleIncrementFactAttribute : FactAttribute
{
    public OfflineDarkSoulsRemasteredSingleIncrementFactAttribute()
    {
        string? confirmation = Environment.GetEnvironmentVariable(
            DarkSoulsRemasteredSingleIncrementValidationHarness.ConfirmationEnvironmentVariable);
        if (!string.Equals(
                confirmation,
                DarkSoulsRemasteredSingleIncrementValidationHarness.RequiredConfirmation,
                StringComparison.Ordinal))
        {
            Skip = "Requires explicit per-run confirmation after QA has received the user's offline-game-open and manual-death confirmation.";
        }
    }
}
