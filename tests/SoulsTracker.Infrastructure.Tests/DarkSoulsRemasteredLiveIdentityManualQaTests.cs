using SoulsTracker.Infrastructure;
using Xunit.Abstractions;

namespace SoulsTracker.Infrastructure.Tests;

/// <summary>
/// This test is inert unless QA explicitly supplies the per-run confirmation
/// after the user has confirmed that the offline game is already open.
/// </summary>
public sealed class DarkSoulsRemasteredLiveIdentityManualQaTests
{
    private readonly ITestOutputHelper output;

    public DarkSoulsRemasteredLiveIdentityManualQaTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [OfflineDarkSoulsRemasteredLiveIdentityFact]
    [Trait("Category", "ManualLiveValidation")]
    public async Task ValidatesOnlyTheUserConfirmedOfflineCandidate()
    {
        string? confirmation = Environment.GetEnvironmentVariable(
            DarkSoulsRemasteredLiveIdentityValidationHarness.ConfirmationEnvironmentVariable);
        DarkSoulsRemasteredCandidateIdentityValidationResult? result = await
            DarkSoulsRemasteredLiveIdentityValidationHarness.CreateForManualQaRun().RunIfConfirmedAsync(
                confirmation,
                CancellationToken.None);

        Assert.NotNull(result);
        output.WriteLine($"Dark Souls Remastered live identity outcome: {result.Outcome}");
        Assert.Equal(DarkSoulsRemasteredCandidateIdentityValidationOutcome.MatchedCandidate, result.Outcome);
    }
}

/// <summary>
/// The test framework reports this test as skipped unless the explicit,
/// user-coordinated confirmation is present before test discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class OfflineDarkSoulsRemasteredLiveIdentityFactAttribute : FactAttribute
{
    public OfflineDarkSoulsRemasteredLiveIdentityFactAttribute()
    {
        string? confirmation = Environment.GetEnvironmentVariable(
            DarkSoulsRemasteredLiveIdentityValidationHarness.ConfirmationEnvironmentVariable);
        if (!string.Equals(
                confirmation,
                DarkSoulsRemasteredLiveIdentityValidationHarness.RequiredConfirmation,
                StringComparison.Ordinal))
        {
            Skip = "Requires explicit per-run confirmation after QA has received the user's offline-game-open confirmation.";
        }
    }
}
