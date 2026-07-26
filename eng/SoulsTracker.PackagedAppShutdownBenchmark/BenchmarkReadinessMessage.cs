using System.Text.Json;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed record BenchmarkReadinessMessage(int ProcessId, Uri OverlayUrl)
{
    internal const int ProtocolSchemaVersion = 2;
    internal const string PreviewReadyMessageType = "preview-ready";
    internal const string ObsConnectedMessageType = "obs-connected";
    internal const string FinalReadyMessageType = "ready";

    public static BenchmarkReadinessMessage ParsePreview(
        ReadOnlySpan<byte> payload,
        int expectedProcessId)
    {
        WireMessage parsed = Parse(payload);
        if (parsed.SchemaVersion != ProtocolSchemaVersion ||
            !string.Equals(parsed.MessageType, PreviewReadyMessageType, StringComparison.Ordinal) ||
            parsed.ProcessId != expectedProcessId ||
            !Uri.TryCreate(parsed.OverlayUrl, UriKind.Absolute, out Uri? overlayUrl) ||
            !string.Equals(overlayUrl.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.Equals(overlayUrl.Host, "127.0.0.1", StringComparison.Ordinal) ||
            overlayUrl.Port is < 1 or > 65535 ||
            !string.Equals(overlayUrl.AbsolutePath, "/overlay/total_deaths", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(ParseToken(overlayUrl)))
        {
            throw new InvalidDataException(
                "The preview readiness message did not match the launched application.");
        }

        return new BenchmarkReadinessMessage(parsed.ProcessId, overlayUrl);
    }

    public static byte[] CreateAcknowledgement(int expectedProcessId) =>
        JsonSerializer.SerializeToUtf8Bytes(new WireMessage(
            ProtocolSchemaVersion,
            ObsConnectedMessageType,
            expectedProcessId,
            OverlayUrl: null));

    public static void ValidateFinal(ReadOnlySpan<byte> payload, int expectedProcessId)
    {
        WireMessage parsed = Parse(payload);
        if (parsed.SchemaVersion != ProtocolSchemaVersion ||
            !string.Equals(parsed.MessageType, FinalReadyMessageType, StringComparison.Ordinal) ||
            parsed.ProcessId != expectedProcessId ||
            parsed.OverlayUrl is not null)
        {
            throw new InvalidDataException(
                "The final readiness message did not match the launched application.");
        }
    }

    public Uri CreateWebSocketUri()
    {
        var builder = new UriBuilder(OverlayUrl)
        {
            Scheme = Uri.UriSchemeWs,
            Path = "/overlay/ws",
        };
        return builder.Uri;
    }

    private static WireMessage Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<WireMessage>(payload) ??
                throw new InvalidDataException("The readiness message was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The readiness message was invalid.", exception);
        }
    }

    private static string? ParseToken(Uri uri)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(pair[0], "token", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private sealed record WireMessage(
        int SchemaVersion,
        string MessageType,
        int ProcessId,
        string? OverlayUrl);
}
