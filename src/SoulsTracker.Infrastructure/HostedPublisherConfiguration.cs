using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SoulsTracker.Infrastructure;

/// <summary>Private pairing material. No public serializable secret properties.</summary>
public sealed class HostedPublisherConfiguration
{
    internal int Version { get; }
    internal string Origin { get; }
    internal string OverlayId { get; }
    internal string? RequestId { get; }
    internal string ReadCapability { get; }
    internal string WriteCapability { get; }
    public string DisplayOrigin => Origin;

    private HostedPublisherConfiguration(int version, string origin, string overlayId, string? requestId,
        string readCapability, string writeCapability)
    {
        Version = version;
        Origin = origin;
        OverlayId = overlayId;
        RequestId = requestId;
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
        return new(1, origin, overlayId, null, readCapability, writeCapability);
    }

    internal static HostedPublisherConfiguration CreateV2(string origin, string overlayId, string requestId,
        string readCapability, string writeCapability, IEnumerable<string> approvedOrigins)
    {
        ValidateOrigin(origin, approvedOrigins);
        if (!Hex(overlayId, 64) || !Hex(requestId, 64) || !Hex(readCapability, 64) ||
            !Hex(writeCapability, 64) || readCapability == writeCapability)
            throw new ArgumentException("Invalid hosted configuration.");
        return new(2, origin, overlayId, requestId, readCapability, writeCapability);
    }

    private static void ValidateOrigin(string origin, IEnumerable<string> approvedOrigins)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.GetLeftPart(UriPartial.Authority) != origin || !string.IsNullOrEmpty(uri.UserInfo) ||
            !approvedOrigins.Contains(origin, StringComparer.Ordinal))
            throw new ArgumentException("Invalid or unapproved hosted pairing configuration.");
    }

    internal static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static HostedPublisherConfiguration Generate(string origin, string overlayId, IEnumerable<string> approvedOrigins)
    {
        string read = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string write;
        do write = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); while (write == read);
        return Create(origin, overlayId, read, write, approvedOrigins);
    }
    internal static string Verifier(string overlayId, string role, string capability) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes($"overlay-v1:{overlayId}:{role}:{capability}")));
    internal static string VerifierV2(string requestId, string role, string capability) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes($"overlay-v2:{requestId}:{role}:{capability}")));
    public string BuildReadUrl() => $"{Origin}/overlay/#id={OverlayId}&read={ReadCapability}";
    public override string ToString() => "Hosted publisher configuration (protected)";

    internal byte[] Encode() => Version == 1
        ? JsonSerializer.SerializeToUtf8Bytes(new { version = 1, origin = Origin, overlayId = OverlayId,
            readCapability = ReadCapability, writeCapability = WriteCapability })
        : JsonSerializer.SerializeToUtf8Bytes(new { version = 2, origin = Origin, overlayId = OverlayId,
            requestId = RequestId, readCapability = ReadCapability, writeCapability = WriteCapability });

    internal static HostedPublisherConfiguration Decode(byte[] bytes, IEnumerable<string> origins)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out JsonElement version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int parsedVersion)) throw new JsonException();
        string[] fields = parsedVersion == 1
            ? ["version", "origin", "overlayId", "readCapability", "writeCapability"]
            : ["version", "origin", "overlayId", "requestId", "readCapability", "writeCapability"];
        if (root.EnumerateObject().Count() != fields.Length || fields.Any(f => !root.TryGetProperty(f, out _))) throw new JsonException();
        string origin = root.GetProperty("origin").GetString()!, overlayId = root.GetProperty("overlayId").GetString()!;
        string read = root.GetProperty("readCapability").GetString()!, write = root.GetProperty("writeCapability").GetString()!;
        return parsedVersion switch
        {
            1 => Create(origin, overlayId, read, write, origins),
            2 => CreateV2(origin, overlayId, root.GetProperty("requestId").GetString()!, read, write, origins),
            _ => throw new JsonException(),
        };
    }
}
