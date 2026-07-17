using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Application.Tests;

public sealed class ConfirmedLegacyProposalApplicationTests
{
    [Fact]
    public void AcceptedRedactedFixtureMapsOnlyRecognizedValuesToAnEligibleCandidate()
    {
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(ReadAcceptedFixture());

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);

        Assert.Equal(LegacyProposalApplicationOutcome.Applied, result.Outcome);
        PersistentTrackerState candidate = Assert.IsType<PersistentTrackerState>(result.CandidateState);
        Assert.Equal(GameId.Ds3, candidate.SelectedGameId);
        Assert.True(candidate.BossProgress.IsDefeated(GameId.Ds1, BossId.Parse("asylum_demon")));
        Assert.True(candidate.BossProgress.IsDefeated(GameId.Ds2, BossId.Parse("last_giant")));
        Assert.True(candidate.BossProgress.IsDefeated(GameId.Ds3, BossId.Parse("iudex_gundyr")));
        Assert.True(candidate.BossProgress.IsDefeated(GameId.Sekiro, BossId.Parse("gyoubu_oniwa")));
        Assert.False(candidate.BossProgress.IsDefeated(GameId.Ds1, BossId.Parse("taurus_demon")));
        Assert.Equal(0, candidate.ManualBloodborneDeathCounter.Value);
        Assert.Equal(BossListVisibilityMode.All, candidate.OverlayConfiguration.BossList.VisibilityMode);
    }

    [Fact]
    public void AppliedCandidatePreservesDestinationEndpointAndUnrelatedOverlayOptions()
    {
        OverlayAccessToken token = OverlayAccessToken.Parse("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        var endpoint = new OverlayEndpointConfiguration(45781, token);
        var totalDeaths = new TotalDeathsOverlayOptions(isEnabled: false, showGameName: false);
        var existingBossList = new BossListOverlayOptions(isEnabled: false, BossListVisibilityMode.Remaining);
        var overlay = new OverlayConfiguration(OverlayConfiguration.CurrentSchemaVersion, endpoint, totalDeaths, existingBossList);
        var destination = new PersistentTrackerState(
            PersistentTrackerState.CurrentSchemaVersion,
            selectedGameId: null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            overlay);
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(ReadAcceptedFixture());

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, destination);

        PersistentTrackerState candidate = Assert.IsType<PersistentTrackerState>(result.CandidateState);
        Assert.Same(endpoint, candidate.OverlayConfiguration.Endpoint);
        Assert.Same(totalDeaths, candidate.OverlayConfiguration.TotalDeaths);
        Assert.Equal(PersistentTrackerState.CurrentSchemaVersion, candidate.SchemaVersion);
        Assert.Equal(OverlayConfiguration.CurrentSchemaVersion, candidate.OverlayConfiguration.SchemaVersion);
        Assert.False(candidate.OverlayConfiguration.TotalDeaths.IsEnabled);
        Assert.False(candidate.OverlayConfiguration.TotalDeaths.ShowGameName);
        Assert.False(candidate.OverlayConfiguration.BossList.IsEnabled);
        Assert.Equal(BossListVisibilityMode.All, candidate.OverlayConfiguration.BossList.VisibilityMode);
        Assert.Equal(0, candidate.ManualBloodborneDeathCounter.Value);
    }

    [Fact]
    public void AmbiguousLegacyDeathCountNeverMapsToManualBloodborneCounter()
    {
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(ReadAcceptedFixture());

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);

        Assert.Contains(analysis.Report.Issues, issue => issue.Code == LegacyAnalysisIssueCode.AmbiguousDeathCount);
        Assert.Equal(0, Assert.IsType<PersistentTrackerState>(result.CandidateState).ManualBloodborneDeathCounter.Value);
    }

    [Fact]
    public void MissingProposalVisibilityModePreservesTheDestinationBossListOptions()
    {
        var proposal = new LegacyImportProposal(
            selectedGameId: null,
            new Dictionary<GameId, IReadOnlyList<BossId>>(),
            bossListVisibilityMode: null);
        var analysis = new LegacyStateAnalysis(isRejected: false, proposal, new LegacyImportReport([]));
        var bossList = new BossListOverlayOptions(isEnabled: false, BossListVisibilityMode.Remaining);
        var overlay = new OverlayConfiguration(
            OverlayConfiguration.CurrentSchemaVersion,
            OverlayEndpointConfiguration.Unassigned,
            TotalDeathsOverlayOptions.Default,
            bossList);
        var destination = new PersistentTrackerState(
            PersistentTrackerState.CurrentSchemaVersion,
            selectedGameId: null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            overlay);

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, destination);

        PersistentTrackerState candidate = Assert.IsType<PersistentTrackerState>(result.CandidateState);
        Assert.Same(overlay, candidate.OverlayConfiguration);
        Assert.Same(bossList, candidate.OverlayConfiguration.BossList);
        Assert.False(candidate.OverlayConfiguration.BossList.IsEnabled);
        Assert.Equal(BossListVisibilityMode.Remaining, candidate.OverlayConfiguration.BossList.VisibilityMode);
    }

    [Fact]
    public void RejectedAnalysisIsRefusedWithoutChangingOrReturningTheDestination()
    {
        LegacyStateAnalysis rejected = LegacyStateAnalyzer.Analyze("{\"unfinished\":"u8.ToArray());
        PersistentTrackerState destination = PersistentTrackerState.Default;

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(rejected, destination);

        Assert.Equal(LegacyProposalApplicationOutcome.RejectedAnalysis, result.Outcome);
        Assert.Null(result.CandidateState);
        Assert.Null(destination.SelectedGameId);
        Assert.Equal(0, destination.ManualBloodborneDeathCounter.Value);
    }

    [Theory]
    [MemberData(nameof(NonEmptyDestinations))]
    public void UserOwnedDestinationContentIsRefused(PersistentTrackerState destination, LegacyProposalApplicationOutcome expectedOutcome)
    {
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(ReadAcceptedFixture());

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, destination);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Null(result.CandidateState);
    }

    [Fact]
    public void MalformedConstructedProposalIsRefusedWithoutExceptionDetails()
    {
        LegacyImportProposal proposal = new(
            selectedGameId: null,
            new Dictionary<GameId, IReadOnlyList<BossId>>
            {
                [GameId.Ds1] = [BossId.Parse("iudex_gundyr")],
            },
            bossListVisibilityMode: (BossListVisibilityMode)999);
        var analysis = new LegacyStateAnalysis(isRejected: false, proposal, new LegacyImportReport([]));

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);

        Assert.Equal(LegacyProposalApplicationOutcome.InvalidProposal, result.Outcome);
        Assert.Null(result.CandidateState);
        Assert.DoesNotContain("Exception", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidSelectedGames))]
    public void NonselectableOrUnknownSelectedGameIsRefusedWithoutBossData(GameId selectedGameId)
    {
        LegacyImportProposal proposal = new(
            selectedGameId,
            new Dictionary<GameId, IReadOnlyList<BossId>>(),
            bossListVisibilityMode: BossListVisibilityMode.All);
        var analysis = new LegacyStateAnalysis(isRejected: false, proposal, new LegacyImportReport([]));

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);

        Assert.Equal(LegacyProposalApplicationOutcome.InvalidProposal, result.Outcome);
        Assert.Null(result.CandidateState);
    }

    [Fact]
    public void UnknownProposalGameKeyIsRefusedWhenOtherProposalValuesAreValid()
    {
        LegacyImportProposal proposal = new(
            selectedGameId: null,
            new Dictionary<GameId, IReadOnlyList<BossId>>
            {
                [CreateUnvalidatedGameId("unknown_game")] = [],
            },
            bossListVisibilityMode: BossListVisibilityMode.All);
        var analysis = new LegacyStateAnalysis(isRejected: false, proposal, new LegacyImportReport([]));

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, PersistentTrackerState.Default);

        Assert.Equal(LegacyProposalApplicationOutcome.InvalidProposal, result.Outcome);
        Assert.Null(result.CandidateState);
    }

    [Fact]
    public void ResultContractsAreImmutableAndSerializeNoTokenOrLegacySourceDetails()
    {
        const string tokenValue = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(ReadAcceptedFixture());
        var endpoint = new OverlayEndpointConfiguration(45781, OverlayAccessToken.Parse(tokenValue));
        var destination = new PersistentTrackerState(
            PersistentTrackerState.CurrentSchemaVersion,
            null,
            ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
            BossProgress.Empty,
            new OverlayConfiguration(
                OverlayConfiguration.CurrentSchemaVersion,
                endpoint,
                TotalDeathsOverlayOptions.Default,
                BossListOverlayOptions.Default));

        LegacyProposalApplicationResult result = ConfirmedLegacyProposalApplication.Apply(analysis, destination);
        string serialized = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(tokenValue, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("death_sfx_path", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("area-0-0-0:2576", serialized, StringComparison.Ordinal);
        Assert.All(
            typeof(LegacyProposalApplicationResult).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.False(property.CanWrite));
        Assert.DoesNotContain(
            typeof(LegacyProposalApplicationResult).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.PropertyType == typeof(Exception) ||
                property.Name.Contains("Path", StringComparison.Ordinal) ||
                property.Name.Contains("Source", StringComparison.Ordinal) ||
                property.Name.Contains("Backup", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(LegacyProposalApplicationResult).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.SetMethod?.IsPublic == true &&
                !property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)));
    }

    [Fact]
    public void ApplicationSourceHasNoPersistenceDiscoveryOrDesktopWiring()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "SoulsTracker.Application",
            "ConfirmedLegacyProposalApplication.cs"));
        string source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ITrackerStateRepository", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SerializedTrackerCoordinator", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SoulsTracker.Desktop", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LegacyImportPreflight", source, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> NonEmptyDestinations()
    {
        yield return
        [
            new PersistentTrackerState(
                PersistentTrackerState.CurrentSchemaVersion,
                GameId.Ds1,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
                BossProgress.Empty,
                OverlayConfiguration.Default),
            LegacyProposalApplicationOutcome.DestinationHasSelectedGame,
        ];
        yield return
        [
            new PersistentTrackerState(
                PersistentTrackerState.CurrentSchemaVersion,
                selectedGameId: null,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne),
                BossProgress.Empty.MarkDefeated(GameId.Ds1, BossId.Parse("asylum_demon")),
                OverlayConfiguration.Default),
            LegacyProposalApplicationOutcome.DestinationHasDefeatedBossProgress,
        ];
        yield return
        [
            new PersistentTrackerState(
                PersistentTrackerState.CurrentSchemaVersion,
                selectedGameId: null,
                ManualBloodborneDeathCounter.CreateFor(GameId.Bloodborne, 1),
                BossProgress.Empty,
                OverlayConfiguration.Default),
            LegacyProposalApplicationOutcome.DestinationHasManualBloodborneDeaths,
        ];
    }

    public static IEnumerable<object[]> InvalidSelectedGames()
    {
        yield return [GameId.EldenRing];
        yield return [CreateUnvalidatedGameId("unknown_game")];
    }

    private static GameId CreateUnvalidatedGameId(string value)
    {
        ConstructorInfo constructor = typeof(GameId).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException("Test setup could not locate GameId's private constructor.");
        return (GameId)constructor.Invoke([value]);
    }

    private static byte[] ReadAcceptedFixture()
    {
        string fixturePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "docs",
            "fixtures",
            "legacy-state.real-redacted.json"));
        return File.ReadAllBytes(fixturePath);
    }
}
