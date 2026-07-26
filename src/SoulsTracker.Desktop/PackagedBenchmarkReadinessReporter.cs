using System.IO.Pipes;
using System.Text.Json;

namespace SoulsTracker.Desktop;

internal static class PackagedBenchmarkReadinessReporter
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    public static async Task ReportAsync(
        string pipeName,
        string overlayUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new ReadinessPayload(
            SchemaVersion: 1,
            ProcessId: Environment.ProcessId,
            OverlayUrl: overlayUrl));
        await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
    }

    private sealed record ReadinessPayload(
        int SchemaVersion,
        int ProcessId,
        string OverlayUrl);
}
