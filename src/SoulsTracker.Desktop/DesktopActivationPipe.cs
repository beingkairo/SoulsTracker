using System.IO;
using System.IO.Pipes;
using System.Text;

namespace SoulsTracker.Desktop;

/// <summary>Same-user, local restore signal. The mutex remains the sole startup gate.</summary>
internal sealed class DesktopActivationPipe : IDisposable
{
    internal const string ProductionName = "SoulsTracker.Activate.v1";
    private readonly string name;
    private readonly Action restore;
    private readonly CancellationTokenSource cancellation = new();
    private Task? listener;
    private NamedPipeServerStream? pending;

    internal DesktopActivationPipe(string name, Action restore)
    {
        this.name = name;
        this.restore = restore;
    }

    public void Start()
    {
        // Construct the first server synchronously: a duplicate immediately after
        // mutex acquisition must not race listener creation.
        pending = CreateServer();
        listener = ListenAsync(pending);
    }

    private NamedPipeServerStream CreateServer() => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream server)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(cancellation.Token).ConfigureAwait(false);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var reader = new StreamReader(server, Encoding.ASCII, leaveOpen: true);
                    using var writer = new StreamWriter(server, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
                    string? request = await reader.ReadLineAsync(requestTimeout.Token).ConfigureAwait(false);
                    if (request == "OPEN" && !cancellation.IsCancellationRequested)
                    {
                        restore();
                        await writer.WriteLineAsync("OK".AsMemory(), requestTimeout.Token).ConfigureAwait(false);
                    }
                }
                catch (IOException) when (!cancellation.IsCancellationRequested) { /* Broken client. Keep listening. */ }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { /* Stalled client. */ }
                server.Dispose();
                server = CreateServer();
                pending = server;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        catch (IOException) when (cancellation.IsCancellationRequested) { }
        finally { server.Dispose(); }
    }

    internal static async Task<bool> RequestRestoreAsync(string name, TimeSpan timeout)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(cancellation.Token);
            using var reader = new StreamReader(client, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(client, Encoding.ASCII, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("OPEN".AsMemory(), cancellation.Token);
            return await reader.ReadLineAsync(cancellation.Token) == "OK";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (cancellation.IsCancellationRequested) return;
        cancellation.Cancel();
        pending?.Dispose();
        cancellation.Dispose();
    }
}
