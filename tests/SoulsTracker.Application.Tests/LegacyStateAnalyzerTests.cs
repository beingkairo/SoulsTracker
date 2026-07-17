using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Application.Tests;

public sealed class LegacyStateAnalyzerTests
{
    [Fact]
    public void AcceptedRedactedFixtureProducesOnlyValidatedProposalValuesAndSafeReportItems()
    {
        byte[] source = ReadAcceptedFixture();
        byte[] before = source.ToArray();

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(source);

        Assert.False(analysis.IsRejected);
        Assert.Equal(before, source);
        LegacyImportProposal proposal = Assert.IsType<LegacyImportProposal>(analysis.Proposal);
        Assert.Equal(GameId.Ds3, proposal.SelectedGameId);
        Assert.Equal(BossListVisibilityMode.All, proposal.BossListVisibilityMode);
        AssertBosses(proposal, GameId.Ds1, "asylum_demon");
        AssertBosses(proposal, GameId.Ds2, "last_giant");
        AssertBosses(proposal, GameId.Ds3, "iudex_gundyr");
        AssertBosses(proposal, GameId.Sekiro, "gyoubu_oniwa");
        Assert.DoesNotContain(proposal.DefeatedBossesByGame, pair => pair.Key == GameId.Bloodborne);

        Assert.Contains(analysis.Report.Issues, issue =>
            issue.Code == LegacyAnalysisIssueCode.AmbiguousDeathCount &&
            issue.FieldCategory == "death_count" &&
            issue.ValueKind == JsonValueKind.Number);
        Assert.Contains(analysis.Report.Issues, issue =>
            issue.Code == LegacyAnalysisIssueCode.UnknownBoss &&
            issue.SafeIdentifier == "area-0-0-0:2576");
        Assert.Contains(analysis.Report.Issues, issue =>
            issue.Code == LegacyAnalysisIssueCode.ExcludedAudioConfiguration &&
            issue.FieldCategory == "settings.death_sfx_path");
        Assert.All(analysis.Report.Issues, issue =>
        {
            Assert.Matches("^[0-9A-F]{64}$", issue.ContentFingerprint);
        });
    }

    [Fact]
    public void StreamInputIsReadOnlyAndSourceBytesAreNotChanged()
    {
        byte[] source = ReadAcceptedFixture();
        byte[] before = source.ToArray();
        using ReadOnlyMemoryStream stream = new(source);

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(stream);

        Assert.False(analysis.IsRejected);
        Assert.Equal(before, source);
        Assert.True(stream.WasRead);
    }

    [Fact]
    public void UnknownFieldsAreReportedWithoutRetainingTheirNameOrValue()
    {
        byte[] source = DeriveFixture(root => root["sensitive_field_name"] = "opaque-value");

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(source);

        Assert.False(analysis.IsRejected);
        LegacyAnalysisIssue issue = Assert.Single(
            analysis.Report.Issues,
            issue => issue.Code == LegacyAnalysisIssueCode.UnknownField && issue.FieldCategory == "top_level[*]");
        string report = JsonSerializer.Serialize(analysis.Report);
        Assert.DoesNotContain("sensitive_field_name", report, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-value", report, StringComparison.Ordinal);
        Assert.NotNull(issue.ContentFingerprint);
    }

    [Fact]
    public void InvalidBossListModeIsWarningAndIsNotProposed()
    {
        byte[] source = DeriveFixture(root =>
            root["layout_state"]!["config"]!["bossOverlay"]!["bossList"]!["visibilityMode"] = "not-a-mode");

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(source);

        Assert.False(analysis.IsRejected);
        Assert.Null(Assert.IsType<LegacyImportProposal>(analysis.Proposal).BossListVisibilityMode);
        Assert.Contains(analysis.Report.Issues, issue =>
            issue.Code == LegacyAnalysisIssueCode.InvalidBossListVisibilityMode);
    }

    [Fact]
    public void ExcludedAudioConfigurationIsReportedWithoutRetainingItsPath()
    {
        const string audioPath = "C:\\safe-test\\death.wav";
        byte[] source = DeriveFixture(root => root["settings"]!["death_sfx_path"] = audioPath);

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(source);

        Assert.Contains(analysis.Report.Issues, issue =>
            issue.Code == LegacyAnalysisIssueCode.ExcludedAudioConfiguration);
        Assert.DoesNotContain(audioPath, JsonSerializer.Serialize(analysis.Report), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedOrTruncatedJsonIsRejectedWithoutAProposalOrExceptionDetails()
    {
        byte[] fixture = ReadAcceptedFixture();
        byte[] source = fixture[..^8];

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(source);

        Assert.True(analysis.IsRejected);
        Assert.Null(analysis.Proposal);
        LegacyAnalysisIssue issue = Assert.Single(analysis.Report.Issues);
        Assert.Equal(LegacyAnalysisIssueCode.MalformedJson, issue.Code);
        Assert.Equal("document", issue.FieldCategory);
        Assert.Equal(JsonValueKind.Undefined, issue.ValueKind);
        Assert.DoesNotContain("JsonException", JsonSerializer.Serialize(analysis.Report), StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeUnknownBossIdentifierIsRedactedToFingerprintOnly()
    {
        const string unsafeIdentifier = "unsafe/identifier";
        byte[] source = DeriveFixture(root =>
            root["boss_defeats_by_game"]!["ds1"]![unsafeIdentifier] = true);

        LegacyStateAnalysis analysis = LegacyStateAnalyzer.Analyze(source);

        LegacyAnalysisIssue issue = Assert.Single(
            analysis.Report.Issues,
            reportIssue => reportIssue.Code == LegacyAnalysisIssueCode.UnknownBoss &&
                reportIssue.SafeIdentifier is null);
        Assert.NotNull(issue.ContentFingerprint);
        Assert.DoesNotContain(unsafeIdentifier, JsonSerializer.Serialize(analysis.Report), StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalAndReportAreImmutableAndDoNotExposePersistenceOrEndpointContracts()
    {
        Type[] contracts =
        [
            typeof(LegacyStateAnalysis),
            typeof(LegacyImportProposal),
            typeof(LegacyImportReport),
            typeof(LegacyAnalysisIssue),
        ];

        Assert.All(
            contracts,
            type => Assert.DoesNotContain(
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public),
                property => property.SetMethod?.IsPublic == true &&
                    !property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit))));
        Assert.DoesNotContain(typeof(LegacyImportProposal).GetProperties(), property =>
            property.PropertyType == typeof(PersistentTrackerState) ||
            property.PropertyType == typeof(OverlayAccessToken) ||
            property.Name.Contains("Endpoint", StringComparison.Ordinal));
    }

    private static void AssertBosses(LegacyImportProposal proposal, GameId gameId, params string[] expectedBossIds)
    {
        IReadOnlyList<BossId> bosses = Assert.Contains(gameId, proposal.DefeatedBossesByGame);
        Assert.Equal(expectedBossIds, bosses.Select(static boss => boss.Value));
    }

    private static byte[] DeriveFixture(Action<JsonObject> mutate)
    {
        JsonNode root = JsonNode.Parse(ReadAcceptedFixture()) ?? throw new InvalidOperationException("Fixture root was missing.");
        JsonObject document = Assert.IsType<JsonObject>(root);
        mutate(document);
        return Encoding.UTF8.GetBytes(document.ToJsonString());
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

    private sealed class ReadOnlyMemoryStream : MemoryStream
    {
        public ReadOnlyMemoryStream(byte[] buffer)
            : base(buffer, writable: false)
        {
        }

        public bool WasRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return base.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Analyzer must not write to the supplied stream.");
    }
}
