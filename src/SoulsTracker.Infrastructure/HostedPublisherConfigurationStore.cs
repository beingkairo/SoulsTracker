using System.Security.Cryptography;

namespace SoulsTracker.Infrastructure;

/// <summary>Explicit isolated path; never shares ordinary tracker persistence or exports.</summary>
public sealed class HostedPublisherConfigurationStore
{
    private readonly string path;
    private readonly IStateSecretProtector protector;
    private readonly string[] origins;

    public HostedPublisherConfigurationStore(string path, IStateSecretProtector protector, IEnumerable<string>? approvedOrigins = null)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An explicit absolute private configuration path is required.");
        this.path = path;
        this.protector = protector;
        origins = (approvedOrigins ?? []).ToArray();
    }

    public async Task SaveAsync(HostedPublisherConfiguration configuration, CancellationToken cancellationToken = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? plaintext = null;
        try
        {
            plaintext = configuration.Encode();
            _ = HostedPublisherConfiguration.Decode(plaintext, origins);
            byte[] encrypted = protector.Protect(plaintext);
            if (encrypted.Length is 0 or > 16384) throw new InvalidDataException();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Unable to save protected hosted configuration; previous configuration retained."); }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads only the explicitly chosen file; never removes the operator's source.</summary>
    public async Task<HostedPublisherConfiguration> ReadPairingAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        byte[]? bytes = null;
        try
        {
            await using var stream = File.OpenRead(sourcePath);
            if (stream.Length is 0 or > 8192) throw new InvalidDataException();
            bytes = new byte[8193];
            int used = 0;
            while (used < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                used += read;
            }
            if (used > 8192) throw new InvalidDataException();
            byte[] payload = bytes[..used];
            try { return HostedPublisherConfiguration.Decode(payload, origins); }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Unable to import pairing. Choose a valid version 1 bundle for an approved HTTPS host."); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(path); }
        catch { throw new InvalidOperationException("Unable to remove protected pairing; previous configuration retained."); }
    }, cancellationToken);

    public async Task<HostedPublisherConfiguration?> LoadAsync(CancellationToken cancellationToken = default)
    {
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            if (stream.Length is 0 or > 16384) throw new InvalidDataException();
            byte[] ciphertext = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            plaintext = protector.Unprotect(ciphertext);
            if (plaintext.Length > 8192) throw new InvalidDataException();
            return HostedPublisherConfiguration.Decode(plaintext, origins);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Unable to load protected hosted configuration; import valid pairing configuration."); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }
}
