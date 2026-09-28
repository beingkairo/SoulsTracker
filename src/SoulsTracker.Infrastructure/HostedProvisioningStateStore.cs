using System.Security.Cryptography;
using System.Text.Json;

namespace SoulsTracker.Infrastructure;

public enum HostedProvisioningPhase
{
    Claiming,
    Acknowledged,
}

/// <summary>Complete recoverable setup request. Secret members are internal to Infrastructure.</summary>
public sealed class HostedProvisioningState
{
    internal string Origin { get; }
    internal string SlotId { get; }
    internal string SetupGrant { get; }
    internal string RequestId { get; }
    internal string ReadVerifier { get; }
    internal string WriteVerifier { get; }
    public HostedPublisherConfiguration Configuration { get; }
    public HostedProvisioningPhase Phase { get; }
    public bool Paused { get; }

    private HostedProvisioningState(string origin, string slotId, string setupGrant, string requestId,
        HostedPublisherConfiguration configuration, string readVerifier, string writeVerifier,
        HostedProvisioningPhase phase, bool paused)
    {
        Origin = origin;
        SlotId = slotId;
        SetupGrant = setupGrant;
        RequestId = requestId;
        Configuration = configuration;
        ReadVerifier = readVerifier;
        WriteVerifier = writeVerifier;
        Phase = phase;
        Paused = paused;
    }

    public static bool IsSetupCodeShape(string? value) => TryReadCode(value, out _, out _);

    public static HostedProvisioningState Create(string origin, string setupCode, IEnumerable<string> approvedOrigins)
    {
        if (!TryReadCode(setupCode, out string? slotId, out string? grant))
            throw new ArgumentException("Invalid setup code.");
        var configuration = HostedPublisherConfiguration.Generate(origin, slotId!, approvedOrigins);
        return new(origin, slotId!, grant!, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), configuration,
            HostedPublisherConfiguration.Verifier(slotId!, "read", configuration.ReadCapability),
            HostedPublisherConfiguration.Verifier(slotId!, "write", configuration.WriteCapability),
            HostedProvisioningPhase.Claiming, paused: false);
    }

    public HostedProvisioningState WithPaused(bool paused) => new(Origin, SlotId, SetupGrant, RequestId,
        Configuration, ReadVerifier, WriteVerifier, Phase, paused);

    public HostedProvisioningState Acknowledged() => new(Origin, SlotId, SetupGrant, RequestId,
        Configuration, ReadVerifier, WriteVerifier, HostedProvisioningPhase.Acknowledged, paused: false);

    private static bool TryReadCode(string? value, out string? slotId, out string? grant)
    {
        slotId = grant = null;
        if (value is null) return false;
        string[] parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != "st1" || !HostedPublisherConfiguration.Hex(parts[1], 32) ||
            !HostedPublisherConfiguration.Hex(parts[2], 64)) return false;
        slotId = parts[1];
        grant = parts[2];
        return true;
    }

    internal byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        origin = Origin,
        slotId = SlotId,
        setupGrant = SetupGrant,
        requestId = RequestId,
        readCapability = Configuration.ReadCapability,
        writeCapability = Configuration.WriteCapability,
        readVerifier = ReadVerifier,
        writeVerifier = WriteVerifier,
        phase = Phase == HostedProvisioningPhase.Claiming ? "claiming" : "acknowledged",
        paused = Paused,
    });

    internal static HostedProvisioningState Decode(byte[] bytes, IEnumerable<string> approvedOrigins)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
        JsonElement root = document.RootElement;
        string[] fields = ["version", "origin", "slotId", "setupGrant", "requestId", "readCapability",
            "writeCapability", "readVerifier", "writeVerifier", "phase", "paused"];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length ||
            fields.Any(field => !root.TryGetProperty(field, out _)) || root.GetProperty("version").GetRawText() != "1")
            throw new JsonException();
        string origin = root.GetProperty("origin").GetString()!;
        string slotId = root.GetProperty("slotId").GetString()!;
        string grant = root.GetProperty("setupGrant").GetString()!;
        string requestId = root.GetProperty("requestId").GetString()!;
        string read = root.GetProperty("readCapability").GetString()!;
        string write = root.GetProperty("writeCapability").GetString()!;
        string readVerifier = root.GetProperty("readVerifier").GetString()!;
        string writeVerifier = root.GetProperty("writeVerifier").GetString()!;
        if (!HostedPublisherConfiguration.Hex(slotId, 32) || !HostedPublisherConfiguration.Hex(grant, 64) ||
            !HostedPublisherConfiguration.Hex(requestId, 32) || !HostedPublisherConfiguration.Hex(readVerifier, 64) ||
            !HostedPublisherConfiguration.Hex(writeVerifier, 64) || readVerifier == writeVerifier ||
            HostedPublisherConfiguration.Verifier(slotId, "setup", grant).Length != 64)
            throw new JsonException();
        var configuration = HostedPublisherConfiguration.Create(origin, slotId, read, write, approvedOrigins);
        if (readVerifier != HostedPublisherConfiguration.Verifier(slotId, "read", read) ||
            writeVerifier != HostedPublisherConfiguration.Verifier(slotId, "write", write)) throw new JsonException();
        HostedProvisioningPhase phase = root.GetProperty("phase").GetString() switch
        {
            "claiming" => HostedProvisioningPhase.Claiming,
            "acknowledged" => HostedProvisioningPhase.Acknowledged,
            _ => throw new JsonException(),
        };
        if (root.GetProperty("paused").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
        return new(origin, slotId, grant, requestId, configuration, readVerifier, writeVerifier, phase,
            root.GetProperty("paused").GetBoolean());
    }

    public override string ToString() => "Hosted provisioning state (protected)";
}

/// <summary>Separate bounded DPAPI record for an exact recoverable setup request.</summary>
public sealed class HostedProvisioningStateStore
{
    private readonly string path;
    private readonly IStateSecretProtector protector;
    private readonly string[] origins;

    public HostedProvisioningStateStore(string path, IStateSecretProtector protector, IEnumerable<string> approvedOrigins)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An explicit absolute pending configuration path is required.");
        this.path = path;
        this.protector = protector;
        origins = approvedOrigins.ToArray();
    }

    public async Task SavePendingAsync(HostedProvisioningState state, CancellationToken cancellationToken = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[]? plaintext = null;
        try
        {
            plaintext = state.Encode();
            _ = HostedProvisioningState.Decode(plaintext, origins);
            byte[] ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length is 0 or > 32768) throw new InvalidDataException();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temporary, ciphertext, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Unable to save protected pending overlay setup; previous state retained."); }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<HostedProvisioningState?> LoadPendingAsync(CancellationToken cancellationToken = default)
    {
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            if (stream.Length is 0 or > 32768) throw new InvalidDataException();
            byte[] ciphertext = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            plaintext = protector.Unprotect(ciphertext);
            if (plaintext.Length is 0 or > 16384) throw new InvalidDataException();
            return HostedProvisioningState.Decode(plaintext, origins);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Unable to load protected pending overlay setup."); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task RemovePendingAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(path); }
        catch { throw new InvalidOperationException("Unable to remove protected pending overlay setup."); }
    }, cancellationToken);
}
