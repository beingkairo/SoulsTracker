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
