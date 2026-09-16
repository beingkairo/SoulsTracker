using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoulsTracker.Application;

/// <summary>Complete effective presentation; revisions are delivery metadata, never totals.</summary>
public sealed record HostedDeath
{
    public required string Revision { get; init; }
    public required string? Value { get; init; }
    public required string Availability { get; init; }
}

/// <summary>A missing update channel leaves that channel unchanged.</summary>
public sealed record HostedOverlayEnvelope(string Type, bool HasDeath, HostedDeath? Death, HostedAppearance? Appearance = null);

/// <summary>Bounded, strict, secret-free hosted JSON. No transport or ordering ownership.</summary>
public static class HostedOverlayJson
{
    public const int MaximumBytes = 8192;
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static HostedOverlayEnvelope Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new JsonException("Message exceeds size limit.");
        using JsonDocument document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        RequireFields(root, ["v", "type", "death", "appearance"], ["v", "type"]);
        RejectDuplicateFields(root);
        if (root.GetProperty("v").GetRawText() != "1" || root.GetProperty("type").ValueKind != JsonValueKind.String)
            throw new JsonException("Unsupported envelope.");
        string type = root.GetProperty("type").GetString()!;
        bool hasDeath = root.TryGetProperty("death", out JsonElement deathElement);
        bool hasAppearance = root.TryGetProperty("appearance", out JsonElement appearanceElement);
        if (type is not ("snapshot" or "update") ||
            (type == "snapshot" && (!hasDeath || !hasAppearance)) ||
            (type == "update" && ((!hasDeath && !hasAppearance) || (hasDeath && deathElement.ValueKind == JsonValueKind.Null))))
            throw new JsonException("Invalid channels.");
        HostedDeath? death = hasDeath && deathElement.ValueKind != JsonValueKind.Null
            ? deathElement.Deserialize<HostedDeath>(Options) : null;
        if (death is not null) ValidateDeath(death);
        HostedAppearance? appearance = hasAppearance
            ? (appearanceElement.Deserialize<HostedAppearance>(Options) ?? throw new JsonException("Missing appearance.")).Normalize() : null;
        return new(type, hasDeath, death, appearance);
    }

    public static string Serialize(HostedOverlayEnvelope envelope)
    {
        if (!envelope.HasDeath && envelope.Death is not null) throw new JsonException("Inconsistent death channel.");
        Dictionary<string, object?> fields = new() { ["v"] = 1, ["type"] = envelope.Type };
        if (envelope.HasDeath) fields.Add("death", envelope.Death);
        if (envelope.Appearance is not null) fields.Add("appearance", envelope.Appearance.Normalize());
        string json = JsonSerializer.Serialize(fields, Options);
        _ = Parse(json);
        return json;
    }

    internal static void ValidateDeath(HostedDeath death)
    {
        ValidateDecimal(death.Revision);
        if (death.Value is not null) ValidateDecimal(death.Value);
        if (death.Availability is not ("available" or "unavailable") ||
            (death.Availability == "available" && death.Value is null) ||
            (death.Availability == "unavailable" && death.Value is not (null or "0"))) throw new JsonException("Invalid availability.");
    }

    internal static void ValidateDecimal(string value)
    {
        if (string.IsNullOrEmpty(value) || (value.Length > 1 && value[0] == '0') ||
            value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new JsonException("Invalid decimal string.");
    }

    private static void RequireFields(JsonElement element, string[] fields, string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Expected object.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!fields.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException("Unknown or duplicate field.");
        if (required.Any(field => !seen.Contains(field))) throw new JsonException("Missing field.");
    }

    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new JsonException("Duplicate field.");
            RejectDuplicateFields(property.Value);
        }
    }
}
