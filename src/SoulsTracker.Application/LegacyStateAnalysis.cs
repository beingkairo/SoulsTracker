using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoulsTracker.Domain;

namespace SoulsTracker.Application;

/// <summary>
/// Analyzes an explicitly supplied legacy state JSON document without locating,
/// modifying, backing up, or applying it.
/// </summary>
public static class LegacyStateAnalyzer
{
    private const string BossDefeatsByGameField = "boss_defeats_by_game";
    private const string SettingsField = "settings";
    private const string LayoutStateField = "layout_state";
    private const string DeathCountField = "death_count";
    private const string SelectedGameField = "selected_game";
    private const string ExcludedAudioField = "death_sfx_path";
    private const string ConfigField = "config";
    private const string BossOverlayField = "bossOverlay";
    private const string BossListField = "bossList";
    private const string VisibilityModeField = "visibilityMode";

    /// <summary>
    /// Reads and analyzes the supplied stream without disposing, rewinding, or writing to it.
    /// </summary>
    public static LegacyStateAnalysis Analyze(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using MemoryStream copy = new();
        source.CopyTo(copy);
        return Analyze(copy.GetBuffer().AsMemory(0, checked((int)copy.Length)));
    }

    /// <summary>
    /// Analyzes supplied JSON bytes without retaining the source payload.
    /// </summary>
    public static LegacyStateAnalysis Analyze(ReadOnlyMemory<byte> source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Rejected(source, LegacyAnalysisIssueCode.UnsupportedRootValue, "document");
            }

            return AnalyzeObject(document.RootElement);
        }
        catch (JsonException)
        {
            return Rejected(source, LegacyAnalysisIssueCode.MalformedJson, "document");
        }
    }

    private static LegacyStateAnalysis AnalyzeObject(JsonElement root)
    {
        List<LegacyAnalysisIssue> issues = [];
        Dictionary<GameId, IReadOnlyList<BossId>> defeatedBossesByGame = [];
        GameId? selectedGameId = null;
        BossListVisibilityMode? bossListVisibilityMode = null;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case BossDefeatsByGameField:
                    AnalyzeBossDefeats(property.Value, defeatedBossesByGame, issues);
                    break;
                case SettingsField:
                    selectedGameId = AnalyzeSettings(property.Value, issues);
                    break;
                case LayoutStateField:
                    bossListVisibilityMode = AnalyzeLayoutState(property.Value, issues);
                    break;
                case DeathCountField:
                    issues.Add(Issue(
                        LegacyAnalysisIssueCode.AmbiguousDeathCount,
                        "death_count",
                        property.Value));
                    break;
                default:
                    issues.Add(Issue(LegacyAnalysisIssueCode.UnknownField, "top_level[*]", property.Value));
                    break;
            }
        }

        return new LegacyStateAnalysis(
            isRejected: false,
            new LegacyImportProposal(selectedGameId, defeatedBossesByGame, bossListVisibilityMode),
            new LegacyImportReport(issues));
    }

    private static void AnalyzeBossDefeats(
        JsonElement value,
        Dictionary<GameId, IReadOnlyList<BossId>> defeatedBossesByGame,
        List<LegacyAnalysisIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Issue(LegacyAnalysisIssueCode.InvalidValue, "boss_defeats_by_game", value));
            return;
        }

        foreach (JsonProperty gameProperty in value.EnumerateObject())
        {
            if (!GameCatalog.TryGet(gameProperty.Name, out GameDefinition? game) || !game.IsSelectable)
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.UnknownGame, "boss_defeats_by_game[*]", gameProperty.Value));
                continue;
            }

            if (gameProperty.Value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.InvalidValue, "boss_defeats_by_game[known_game]", gameProperty.Value));
                continue;
            }

            List<BossId> defeatedBosses = [];
            foreach (JsonProperty bossProperty in gameProperty.Value.EnumerateObject())
            {
                BossDefinition? boss = game.BossCatalog.SingleOrDefault(
                    knownBoss => string.Equals(knownBoss.Id.Value, bossProperty.Name, StringComparison.Ordinal));
                if (boss is null)
                {
                    issues.Add(Issue(
                        LegacyAnalysisIssueCode.UnknownBoss,
                        "boss_defeats_by_game[known_game][*]",
                        bossProperty.Value,
                        SafeIdentifierOrNull(bossProperty.Name)));
                    continue;
                }

                if (bossProperty.Value.ValueKind == JsonValueKind.True)
                {
                    defeatedBosses.Add(boss.Id);
                }
                else if (bossProperty.Value.ValueKind != JsonValueKind.False)
                {
                    issues.Add(Issue(
                        LegacyAnalysisIssueCode.InvalidValue,
                        "boss_defeats_by_game[known_game][known_boss]",
                        bossProperty.Value));
                }
            }

            if (defeatedBosses.Count > 0)
            {
                defeatedBossesByGame[game.Id] = Array.AsReadOnly(defeatedBosses.ToArray());
            }
        }
    }

    private static GameId? AnalyzeSettings(JsonElement value, List<LegacyAnalysisIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Issue(LegacyAnalysisIssueCode.InvalidValue, "settings", value));
            return null;
        }

        GameId? selectedGameId = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name == SelectedGameField)
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    GameCatalog.TryGet(property.Value.GetString(), out GameDefinition? game) &&
                    game.IsSelectable)
                {
                    selectedGameId = game.Id;
                }
                else
                {
                    issues.Add(Issue(LegacyAnalysisIssueCode.InvalidSelectedGame, "settings.selected_game", property.Value));
                }
            }
            else if (property.Name == ExcludedAudioField)
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.ExcludedAudioConfiguration, "settings.death_sfx_path", property.Value));
            }
            else
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.UnknownField, "settings[*]", property.Value));
            }
        }

        return selectedGameId;
    }

    private static BossListVisibilityMode? AnalyzeLayoutState(JsonElement value, List<LegacyAnalysisIssue> issues)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            issues.Add(Issue(LegacyAnalysisIssueCode.InvalidValue, "layout_state", value));
            return null;
        }

        BossListVisibilityMode? visibilityMode = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name == ConfigField && property.Value.ValueKind == JsonValueKind.Object)
            {
                visibilityMode = AnalyzeConfig(property.Value, issues);
            }
            else if (property.Name == ConfigField)
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.InvalidValue, "layout_state.config", property.Value));
            }
            else
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.UnknownField, "layout_state[*]", property.Value));
            }
        }

        return visibilityMode;
    }

    private static BossListVisibilityMode? AnalyzeConfig(JsonElement value, List<LegacyAnalysisIssue> issues)
    {
        BossListVisibilityMode? visibilityMode = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name == BossOverlayField && property.Value.ValueKind == JsonValueKind.Object)
            {
                visibilityMode = AnalyzeBossOverlay(property.Value, issues);
            }
            else if (property.Name == BossOverlayField)
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.InvalidValue, "layout_state.config.bossOverlay", property.Value));
            }
            else
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.UnknownField, "layout_state.config[*]", property.Value));
            }
        }

        return visibilityMode;
    }

    private static BossListVisibilityMode? AnalyzeBossOverlay(JsonElement value, List<LegacyAnalysisIssue> issues)
    {
        BossListVisibilityMode? visibilityMode = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name == BossListField && property.Value.ValueKind == JsonValueKind.Object)
            {
                visibilityMode = AnalyzeBossList(property.Value, issues);
            }
            else if (property.Name == BossListField)
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.InvalidValue, "layout_state.config.bossOverlay.bossList", property.Value));
            }
            else
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.UnknownField, "layout_state.config.bossOverlay[*]", property.Value));
            }
        }

        return visibilityMode;
    }

    private static BossListVisibilityMode? AnalyzeBossList(JsonElement value, List<LegacyAnalysisIssue> issues)
    {
        BossListVisibilityMode? visibilityMode = null;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name != VisibilityModeField)
            {
                issues.Add(Issue(LegacyAnalysisIssueCode.UnknownField, "layout_state.config.bossOverlay.bossList[*]", property.Value));
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String ||
                !TryParseLegacyVisibilityMode(property.Value.GetString(), out BossListVisibilityMode parsedMode))
            {
                issues.Add(Issue(
                    LegacyAnalysisIssueCode.InvalidBossListVisibilityMode,
                    "layout_state.config.bossOverlay.bossList.visibilityMode",
                    property.Value));
                continue;
            }

            visibilityMode = parsedMode;
        }

        return visibilityMode;
    }

    private static bool TryParseLegacyVisibilityMode(string? value, out BossListVisibilityMode visibilityMode)
    {
        switch (value)
        {
            case "all":
                visibilityMode = BossListVisibilityMode.All;
                return true;
            case "remaining":
                visibilityMode = BossListVisibilityMode.Remaining;
                return true;
            case "defeated":
                visibilityMode = BossListVisibilityMode.Defeated;
                return true;
            default:
                visibilityMode = default;
                return false;
        }
    }

    private static LegacyStateAnalysis Rejected(
        ReadOnlyMemory<byte> source,
        LegacyAnalysisIssueCode code,
        string fieldCategory) =>
        new(
            isRejected: true,
            proposal: null,
            new LegacyImportReport([new LegacyAnalysisIssue(code, fieldCategory, JsonValueKind.Undefined, Fingerprint(source.Span), null)]));

    private static LegacyAnalysisIssue Issue(
        LegacyAnalysisIssueCode code,
        string fieldCategory,
        JsonElement value,
        string? safeIdentifier = null) =>
        new(code, fieldCategory, value.ValueKind, Fingerprint(value.GetRawText()), safeIdentifier);

    private static string? SafeIdentifierOrNull(string identifier) =>
        identifier.Length is > 0 and <= 128 &&
        identifier.All(static character =>
            (character >= 'a' && character <= 'z') ||
            (character >= '0' && character <= '9') ||
            character is ':' or '_' or '-')
                ? identifier
                : null;

    private static string Fingerprint(string value) => Fingerprint(Encoding.UTF8.GetBytes(value));

    private static string Fingerprint(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
}

/// <summary>
/// Describes a read-only analysis result. A rejected result intentionally has no proposal.
/// </summary>
public sealed class LegacyStateAnalysis
{
    public LegacyStateAnalysis(bool isRejected, LegacyImportProposal? proposal, LegacyImportReport report)
    {
        if (isRejected == (proposal is not null))
        {
            throw new ArgumentException("Rejected analyses have no proposal and analyzable documents have one.", nameof(proposal));
        }

        ArgumentNullException.ThrowIfNull(report);
        IsRejected = isRejected;
        Proposal = proposal;
        Report = report;
    }

    public bool IsRejected { get; }

    public LegacyImportProposal? Proposal { get; }

    public LegacyImportReport Report { get; }
}

/// <summary>
/// Contains only values that a later confirmation and application workflow may review.
/// </summary>
public sealed class LegacyImportProposal
{
    public LegacyImportProposal(
        GameId? selectedGameId,
        IReadOnlyDictionary<GameId, IReadOnlyList<BossId>> defeatedBossesByGame,
        BossListVisibilityMode? bossListVisibilityMode)
    {
        ArgumentNullException.ThrowIfNull(defeatedBossesByGame);

        SelectedGameId = selectedGameId;
        DefeatedBossesByGame = new ReadOnlyDictionary<GameId, IReadOnlyList<BossId>>(
            defeatedBossesByGame.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<BossId>)Array.AsReadOnly(pair.Value.ToArray())));
        BossListVisibilityMode = bossListVisibilityMode;
    }

    public GameId? SelectedGameId { get; }

    public IReadOnlyDictionary<GameId, IReadOnlyList<BossId>> DefeatedBossesByGame { get; }

    public BossListVisibilityMode? BossListVisibilityMode { get; }
}

/// <summary>
/// Holds secret-safe analysis findings. It never retains source JSON values or paths.
/// </summary>
public sealed class LegacyImportReport
{
    public LegacyImportReport(IEnumerable<LegacyAnalysisIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public IReadOnlyList<LegacyAnalysisIssue> Issues { get; }
}

/// <summary>
/// Classifies a safe report item without exposing an input value.
/// </summary>
public enum LegacyAnalysisIssueCode
{
    MalformedJson,
    UnsupportedRootValue,
    AmbiguousDeathCount,
    UnknownGame,
    UnknownBoss,
    UnknownField,
    InvalidValue,
    InvalidSelectedGame,
    InvalidBossListVisibilityMode,
    ExcludedAudioConfiguration,
}

/// <summary>
/// Records a fixed field category, JSON value kind, opaque fingerprint, and only a
/// conservatively validated unknown-boss identifier when one is safe to retain.
/// </summary>
public sealed record LegacyAnalysisIssue(
    LegacyAnalysisIssueCode Code,
    string FieldCategory,
    JsonValueKind ValueKind,
    string ContentFingerprint,
    string? SafeIdentifier);
