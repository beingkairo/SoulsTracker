using System.Text.Json;

namespace SoulsTracker.PackagedAppShutdownBenchmark;

internal sealed record BenchmarkReadinessMessage(int ProcessId, Uri OverlayUrl)
{
    public static BenchmarkReadinessMessage Parse(ReadOnlySpan<byte> payload, int expectedProcessId)
    {
        ReadinessPayload? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReadinessPayload>(payload);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The readiness payload was invalid.", exception);
        }

        if (parsed is null ||
            parsed.SchemaVersion != 1 ||
            parsed.ProcessId != expectedProcessId ||
            !Uri.TryCreate(parsed.OverlayUrl, UriKind.Absolute, out Uri? overlayUrl) ||
            !string.Equals(overlayUrl.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
            !string.Equals(overlayUrl.Host, "127.0.0.1", StringComparison.Ordinal) ||
            overlayUrl.Port is < 1 or > 65535 ||
            !string.Equals(overlayUrl.AbsolutePath, "/overlay/total_deaths", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(ParseToken(overlayUrl)))
        {
            throw new InvalidDataException("The readiness payload did not match the launched application.");
        }

        return new BenchmarkReadinessMessage(parsed.ProcessId, overlayUrl);
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

    private sealed record ReadinessPayload(int SchemaVersion, int ProcessId, string OverlayUrl);
}
