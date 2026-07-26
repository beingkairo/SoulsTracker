using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace SoulsTracker.Desktop;

internal static class PackagedBenchmarkReadinessReporter
{
    internal const PipeOptions ReadinessPipeOptions =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    internal const int ProtocolSchemaVersion = 2;
    internal const string PreviewReadyMessageType = "preview-ready";
    internal const string ObsConnectedMessageType = "obs-connected";
    internal const string FinalReadyMessageType = "ready";
    private const int MaximumMessageBytes = 16 * 1024;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    public static Task ReportAsync(
        string pipeName,
        string overlayUrl,
        Func<int> activeConnectionCount,
        CancellationToken cancellationToken = default) =>
        ReportAsync(
            pipeName,
            overlayUrl,
            activeConnectionCount,
            ConnectionTimeout,
            cancellationToken);

    internal static async Task ReportAsync(
        string pipeName,
        string overlayUrl,
        Func<int> activeConnectionCount,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayUrl);
        ArgumentNullException.ThrowIfNull(activeConnectionCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var boundedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        boundedCancellation.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            ReadinessPipeOptions);
        await pipe.ConnectAsync(boundedCancellation.Token).ConfigureAwait(false);

        int processId = Environment.ProcessId;
        await WriteMessageAsync(
            pipe,
            new ReadinessMessage(
                ProtocolSchemaVersion,
                PreviewReadyMessageType,
                processId,
                overlayUrl),
            boundedCancellation.Token).ConfigureAwait(false);

        ReadinessMessage acknowledgement = await ReadMessageAsync(
            pipe,
            boundedCancellation.Token).ConfigureAwait(false);
        if (acknowledgement.SchemaVersion != ProtocolSchemaVersion ||
            !string.Equals(
                acknowledgement.MessageType,
                ObsConnectedMessageType,
                StringComparison.Ordinal) ||
            acknowledgement.ProcessId != processId ||
            acknowledgement.OverlayUrl is not null)
        {
            throw new InvalidDataException(
                "The benchmark readiness acknowledgement was invalid.");
        }

        while (activeConnectionCount() < 2)
        {
            await Task.Delay(25, boundedCancellation.Token).ConfigureAwait(false);
        }

        await WriteMessageAsync(
            pipe,
            new ReadinessMessage(
                ProtocolSchemaVersion,
                FinalReadyMessageType,
                processId,
                OverlayUrl: null),
            boundedCancellation.Token).ConfigureAwait(false);
    }

    internal static async Task<ReadinessMessage> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var payload = new MemoryStream();
        byte[] nextByte = new byte[1];
        while (payload.Length <= MaximumMessageBytes)
        {
            int read = await stream.ReadAsync(nextByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The readiness channel closed before a complete message.");
            }

            if (nextByte[0] == (byte)'\n')
            {
                try
                {
                    return JsonSerializer.Deserialize<ReadinessMessage>(payload.ToArray()) ??
                        throw new InvalidDataException("The readiness message was empty.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("The readiness message was invalid.", exception);
                }
            }

            payload.WriteByte(nextByte[0]);
        }

        throw new InvalidDataException("The readiness message was too large.");
    }

    internal static async Task WriteMessageAsync(
        Stream stream,
        ReadinessMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaximumMessageBytes)
        {
            throw new InvalidDataException("The readiness message was too large.");
        }

        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal sealed record ReadinessMessage(
        int SchemaVersion,
        string MessageType,
        int ProcessId,
        string? OverlayUrl);
}
