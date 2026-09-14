using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Application;

public static class LegacyStateAnalyzer
{
    public static LegacyStateAnalysis Analyze(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using MemoryStream copy = new(); source.CopyTo(copy);
        return Analyze(copy.ToArray());
    }

    public static LegacyStateAnalysis Analyze(ReadOnlyMemory<byte> source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Rejected(source, LegacyAnalysisIssueCode.UnsupportedRootValue);
            GameId? selected = null;
            List<LegacyAnalysisIssue> issues = [];
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "settings" && property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("selected_game", out JsonElement game) && game.ValueKind == JsonValueKind.String)
                {
                    if (GameCatalog.TryGet(game.GetString(), out GameDefinition? definition) && definition.IsSelectable) selected = definition.Id;
                    else issues.Add(Issue(LegacyAnalysisIssueCode.InvalidSelectedGame, "settings.selected_game", game));
                }
                else if (property.Name == "death_count") issues.Add(Issue(LegacyAnalysisIssueCode.AmbiguousDeathCount, "death_count", property.Value));
                else if (property.Name is not "settings" and not "config" and not "layout_state") issues.Add(Issue(LegacyAnalysisIssueCode.UnknownField, "top_level[*]", property.Value));
            }
            return new LegacyStateAnalysis(false, new LegacyImportProposal(selected), new LegacyImportReport(issues));
        }
        catch (JsonException) { return Rejected(source, LegacyAnalysisIssueCode.MalformedJson); }
    }

    private static LegacyStateAnalysis Rejected(ReadOnlyMemory<byte> source, LegacyAnalysisIssueCode code) => new(true, null, new LegacyImportReport([new(code, "document", JsonValueKind.Undefined, Fingerprint(source.Span), null)]));
    private static LegacyAnalysisIssue Issue(LegacyAnalysisIssueCode code, string field, JsonElement value) => new(code, field, value.ValueKind, Fingerprint(value.GetRawText()), null);
    private static string Fingerprint(string value) => Fingerprint(Encoding.UTF8.GetBytes(value));
    private static string Fingerprint(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
}

public sealed class LegacyStateAnalysis(bool isRejected, LegacyImportProposal? proposal, LegacyImportReport report)
{
    public bool IsRejected { get; } = isRejected;
    public LegacyImportProposal? Proposal { get; } = proposal;
    public LegacyImportReport Report { get; } = report ?? throw new ArgumentNullException(nameof(report));
}

public sealed class LegacyImportProposal(GameId? selectedGameId)
{
    public GameId? SelectedGameId { get; } = selectedGameId;
}

public sealed class LegacyImportReport(IEnumerable<LegacyAnalysisIssue> issues)
{
    public IReadOnlyList<LegacyAnalysisIssue> Issues { get; } = Array.AsReadOnly((issues ?? throw new ArgumentNullException(nameof(issues))).ToArray());
}

public enum LegacyAnalysisIssueCode { MalformedJson, UnsupportedRootValue, AmbiguousDeathCount, UnknownGame, UnknownBoss, UnknownField, InvalidValue, InvalidSelectedGame, InvalidBossListVisibilityMode, ExcludedAudioConfiguration }
public sealed record LegacyAnalysisIssue(LegacyAnalysisIssueCode Code, string FieldCategory, JsonValueKind ValueKind, string ContentFingerprint, string? SafeIdentifier);
