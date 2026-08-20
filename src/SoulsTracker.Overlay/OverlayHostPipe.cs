using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulsTracker.Application;
using SoulsTracker.Domain;

namespace SoulsTracker.Overlay;

/// <summary>Bounded, current-user-only pipe transport between the desktop and optional sign-in host.</summary>
public static class OverlayHostPipe
{
    private const int ProofLength = 43;
    private const int MaximumPayloadLength = 1_048_576;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string NameFor(int port) => $"SoulsTracker.OverlayHost.{port}";

    public static async Task RunServerAsync(PersistentOverlayHost host, Action requestStop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(requestStop);
        string name = NameFor(host.Port);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            await HandleConnectionAsync(host, requestStop, pipe, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<bool> SendSnapshotAsync(IOverlayEndpointAccess endpoint, OverlaySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(snapshot);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonOptions);
        return await SendAsync(endpoint, 1, json, cancellationToken).ConfigureAwait(false);
    }

    public static Task<bool> ClearAsync(IOverlayEndpointAccess endpoint, CancellationToken cancellationToken = default) =>
        SendAsync(endpoint ?? throw new ArgumentNullException(nameof(endpoint)), 2, [], cancellationToken);

    public static Task<bool> StopAsync(IOverlayEndpointAccess endpoint, CancellationToken cancellationToken = default) =>
        SendAsync(endpoint ?? throw new ArgumentNullException(nameof(endpoint)), 3, [], cancellationToken);

    private static async Task<bool> SendAsync(IOverlayEndpointAccess endpoint, byte kind, byte[] payload, CancellationToken cancellationToken)
    {
        if (!endpoint.Configuration.IsAssigned || payload.Length > MaximumPayloadLength) return false;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", NameFor(endpoint.Configuration.Port!.Value), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1_000, cancellationToken).ConfigureAwait(false);
            byte[] proof = Encoding.ASCII.GetBytes(endpoint.BuildLocalHostProof());
            if (proof.Length != ProofLength) return false;
            await pipe.WriteAsync(new byte[] { kind }, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(proof, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), cancellationToken).ConfigureAwait(false);
            if (payload.Length > 0) await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            int result = pipe.ReadByte();
            return result == 1;
        }
        catch (IOException) { return false; }
        catch (TimeoutException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task HandleConnectionAsync(PersistentOverlayHost host, Action requestStop, NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            int kind = pipe.ReadByte();
            byte[] proof = await ReadExactAsync(pipe, ProofLength, cancellationToken).ConfigureAwait(false);
            byte[] lengthBytes = await ReadExactAsync(pipe, sizeof(int), cancellationToken).ConfigureAwait(false);
            int length = BitConverter.ToInt32(lengthBytes);
            bool accepted = length is >= 0 and <= MaximumPayloadLength && kind is 1 or 2 or 3;
            byte[] payload = accepted ? await ReadExactAsync(pipe, length, cancellationToken).ConfigureAwait(false) : [];
            string suppliedProof = Encoding.ASCII.GetString(proof);
            accepted &= kind == 1 ? host.AcceptLivePayload(suppliedProof, payload) : length == 0 && kind == 2 ? host.ShowPlaceholder(suppliedProof) : length == 0 && host.ShowPlaceholder(suppliedProof);
            pipe.WriteByte(accepted ? (byte)1 : (byte)0);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (accepted && kind == 3) requestStop();
        }
        catch (IOException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The local overlay-host pipe closed before a complete message arrived.");
            offset += read;
        }
        return buffer;
    }
}
