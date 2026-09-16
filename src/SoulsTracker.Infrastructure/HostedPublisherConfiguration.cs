using System.Text.Json;

namespace SoulsTracker.Infrastructure;

/// <summary>Private pairing material. No public serializable secret properties.</summary>
public sealed class HostedPublisherConfiguration
{
    internal string Origin { get; }
    internal string OverlayId { get; }
    internal string ReadCapability { get; }
    internal string WriteCapability { get; }
    public string DisplayOrigin => Origin;

    private HostedPublisherConfiguration(string origin, string overlayId, string readCapability, string writeCapability)
    {
        Origin = origin;
        OverlayId = overlayId;
        ReadCapability = readCapability;
        WriteCapability = writeCapability;
    }

    public static HostedPublisherConfiguration Create(string origin, string overlayId, string readCapability,
        string writeCapability, IEnumerable<string>? approvedOrigins = null)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.GetLeftPart(UriPartial.Authority) != origin || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(approvedOrigins ?? []).Contains(origin, StringComparer.Ordinal) ||
            !Hex(overlayId, 32) || !Hex(readCapability, 64) || !Hex(writeCapability, 64) || readCapability == writeCapability)
            throw new ArgumentException("Invalid or unapproved hosted pairing configuration.");
        return new(origin, overlayId, readCapability, writeCapability);
    }

    internal static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public string BuildReadUrl() => $"{Origin}/overlay/#id={OverlayId}&read={ReadCapability}";
    public override string ToString() => "Hosted publisher configuration (protected)";

    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        origin = Origin,
        overlayId = OverlayId,
        readCapability = ReadCapability,
        writeCapability = WriteCapability,
    });

    internal static HostedPublisherConfiguration Decode(byte[] bytes, IEnumerable<string> origins)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
        var root = document.RootElement;
        string[] fields = ["version", "origin", "overlayId", "readCapability", "writeCapability"];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length ||
            fields.Any(f => !root.TryGetProperty(f, out _)) || root.GetProperty("version").GetRawText() != "1")
            throw new JsonException();
        return Create(root.GetProperty("origin").GetString()!, root.GetProperty("overlayId").GetString()!,
            root.GetProperty("readCapability").GetString()!, root.GetProperty("writeCapability").GetString()!, origins);
    }
}
