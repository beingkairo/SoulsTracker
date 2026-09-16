using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoulsTracker.Application;

namespace SoulsTracker.Infrastructure;

internal static class HostedPublisherProtocol
{
    internal sealed record Channel(string Revision, string Digest);
    internal sealed record Ack(string Epoch, string Generation, string? Session, string? Sequence, Channel Death, Channel Appearance, string[] Changed);

    internal static string Digest(object? value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value))));

    // JSON.stringify's string encoding, including literal non-ASCII code points.
    // Property order comes from the accepted Application contracts, not incoming JSON.
    internal static string Canonical(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value, HostedOverlayJson.Options);
        var text = new StringBuilder();
        Append(element, text);
        return text.ToString();
    }

    private static void Append(JsonElement element, StringBuilder text)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            text.Append('{');
            bool first = true;
            foreach (var property in element.EnumerateObject())
            {
                if (!first) text.Append(',');
                first = false;
                Quote(property.Name, text);
                text.Append(':');
                Append(property.Value, text);
            }
            text.Append('}');
        }
        else if (element.ValueKind == JsonValueKind.String) Quote(element.GetString()!, text);
        else text.Append(element.GetRawText());
    }

    private static void Quote(string value, StringBuilder text)
    {
        text.Append('"');
        foreach (char c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }
        text.Append('"');
    }

    internal static string State(string epoch, string session, long sequence, HostedDeath? death, HostedAppearance? appearance)
    {
        Dictionary<string, object?> fields = new()
        {
            ["v"] = 1,
            ["epoch"] = epoch,
            ["sessionRequestId"] = session,
            ["sequence"] = sequence.ToString(CultureInfo.InvariantCulture),
        };
        if (death is not null) fields.Add("death", WithoutRevision(death));
        if (appearance is not null) fields.Add("appearance", WithoutRevision(appearance));
        string json = Canonical(fields);
        if (Encoding.UTF8.GetByteCount(json) > HostedOverlayJson.MaximumBytes) throw new JsonException();
        return json;
    }

    private static Dictionary<string, JsonElement> WithoutRevision(object value) => JsonSerializer.SerializeToElement(value, HostedOverlayJson.Options)
        .EnumerateObject().Where(p => p.Name != "revision").ToDictionary(p => p.Name, p => p.Value);

    internal static Ack Parse(byte[] bytes, string action)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 4 });
        var root = document.RootElement;
        string[] fields = action switch
        {
            "publisher" => ["v", "epoch", "generation", "death", "appearance"],
            "session" => ["v", "epoch", "generation", "death", "appearance", "sessionRequestId"],
            _ => ["v", "epoch", "generation", "death", "appearance", "sessionRequestId", "sequence", "changed"],
        };
        Shape(root, fields);
        if (root.GetProperty("v").GetRawText() != "1") throw new JsonException();
        string epoch = Decimal(root, "epoch"), generation = Decimal(root, "generation");
        string? session = action == "publisher" ? null : root.GetProperty("sessionRequestId").GetString();
        if (action != "publisher" && !HostedPublisherConfiguration.Hex(session, 32)) throw new JsonException();
        string? sequence = action == "state" ? Decimal(root, "sequence") : null;
        HashSet<string> changedChannels = [];
        if (action == "state")
        {
            var changed = root.GetProperty("changed");
            if (changed.ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var item in changed.EnumerateArray())
                if (item.GetString() is not ("death" or "appearance") || !changedChannels.Add(item.GetString()!)) throw new JsonException();
        }
        return new(epoch, generation, session, sequence, ReadChannel(root.GetProperty("death")), ReadChannel(root.GetProperty("appearance")), changedChannels.ToArray());
    }

    private static Channel ReadChannel(JsonElement element)
    {
        Shape(element, ["revision", "digest"]);
        string revision = Decimal(element, "revision");
        string? digest = element.GetProperty("digest").GetString();
        if (!HostedPublisherConfiguration.Hex(digest, 64)) throw new JsonException();
        return new(revision, digest!);
    }

    private static string Decimal(JsonElement element, string field)
    {
        string value = element.GetProperty(field).GetString()!;
        HostedOverlayJson.ValidateDecimal(value);
        return value;
    }

    private static void Shape(JsonElement element, string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != fields.Length || fields.Any(f => !names.Contains(f, StringComparer.Ordinal))) throw new JsonException();
    }
}
