using System.Security.Cryptography;
using System.Text.Json;

namespace SoulsTracker.Infrastructure;

public enum HostedProvisioningPhase
{
    Claiming,
    Acknowledged,
}

/// <summary>Complete recoverable anonymous create request. Secret members stay inside Infrastructure.</summary>
public sealed class HostedProvisioningState
{
    internal string Origin { get; }
    internal string RequestId { get; }
    internal string ReadCapability { get; }
    internal string WriteCapability { get; }
    internal string ReadVerifier { get; }
    internal string WriteVerifier { get; }
    internal string? OverlayId { get; }
    private string? LegacySetupGrant { get; }
    public HostedPublisherConfiguration? PriorConfiguration { get; }
    public bool IsPreReleaseVersion1 { get; }
    public HostedProvisioningPhase Phase { get; }

    private HostedProvisioningState(string origin, string requestId, string readCapability, string writeCapability,
        string readVerifier, string writeVerifier, HostedProvisioningPhase phase, string? overlayId,
        HostedPublisherConfiguration? priorConfiguration = null, bool isPreReleaseVersion1 = false,
        string? legacySetupGrant = null)
    {
        Origin = origin;
        RequestId = requestId;
        ReadCapability = readCapability;
        WriteCapability = writeCapability;
        ReadVerifier = readVerifier;
        WriteVerifier = writeVerifier;
        Phase = phase;
        OverlayId = overlayId;
        PriorConfiguration = priorConfiguration;
        IsPreReleaseVersion1 = isPreReleaseVersion1;
        LegacySetupGrant = legacySetupGrant;
    }

    public static HostedProvisioningState Create(string origin, IEnumerable<string> approvedOrigins,
        Func<int, byte[]>? entropy = null)
    {
        entropy ??= RandomNumberGenerator.GetBytes;
        string requestId = Next(entropy), read = Next(entropy), write = Next(entropy);
        if (requestId == read || requestId == write || read == write) throw new InvalidOperationException("Independent provisioning values required.");
        _ = HostedPublisherConfiguration.CreateV2(origin, new string('0', 64), requestId, read, write, approvedOrigins);
        return new(origin, requestId, read, write, HostedPublisherConfiguration.VerifierV2(requestId, "read", read),
            HostedPublisherConfiguration.VerifierV2(requestId, "write", write), HostedProvisioningPhase.Claiming, null);
    }

    private static string Next(Func<int, byte[]> entropy)
    {
        byte[] bytes = entropy(32);
        if (bytes.Length != 32) throw new InvalidOperationException("Provisioning entropy must contain 32 bytes.");
        try { return Convert.ToHexStringLower(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public HostedProvisioningState Acknowledged(string overlayId)
    {
        int expectedLength = IsPreReleaseVersion1 ? 32 : 64;
        if (!HostedPublisherConfiguration.Hex(overlayId, expectedLength) ||
            (IsPreReleaseVersion1 && PriorConfiguration?.OverlayId != overlayId))
            throw new ArgumentException("Invalid overlay identity.");
        return new(Origin, RequestId, ReadCapability, WriteCapability, ReadVerifier, WriteVerifier,
            HostedProvisioningPhase.Acknowledged, overlayId, PriorConfiguration, IsPreReleaseVersion1,
            LegacySetupGrant);
    }

    public HostedPublisherConfiguration Configuration(IEnumerable<string> approvedOrigins) =>
        IsPreReleaseVersion1 && PriorConfiguration is not null ? PriorConfiguration :
        HostedPublisherConfiguration.CreateV2(Origin, OverlayId ?? throw new InvalidOperationException(), RequestId,
            ReadCapability, WriteCapability, approvedOrigins);

    public bool Matches(HostedPublisherConfiguration configuration) =>
        (IsPreReleaseVersion1 && PriorConfiguration?.Origin == configuration.Origin &&
            PriorConfiguration.OverlayId == configuration.OverlayId &&
            PriorConfiguration.ReadCapability == configuration.ReadCapability &&
            PriorConfiguration.WriteCapability == configuration.WriteCapability) ||
        (!IsPreReleaseVersion1 && OverlayId == configuration.OverlayId && RequestId == configuration.RequestId &&
            ReadCapability == configuration.ReadCapability && WriteCapability == configuration.WriteCapability);

    internal byte[] Encode() => IsPreReleaseVersion1
        ? JsonSerializer.SerializeToUtf8Bytes(new { version = 1, origin = Origin,
            slotId = PriorConfiguration?.OverlayId, setupGrant = LegacySetupGrant, requestId = RequestId,
            readCapability = ReadCapability, writeCapability = WriteCapability, readVerifier = ReadVerifier,
            writeVerifier = WriteVerifier, phase = Phase == HostedProvisioningPhase.Claiming ? "claiming" : "acknowledged",
            paused = false })
        : JsonSerializer.SerializeToUtf8Bytes(new { version = 2, origin = Origin,
            requestId = RequestId, readCapability = ReadCapability, writeCapability = WriteCapability,
            readVerifier = ReadVerifier, writeVerifier = WriteVerifier,
            phase = Phase == HostedProvisioningPhase.Claiming ? "claiming" : "acknowledged", overlayId = OverlayId });

    internal static HostedProvisioningState Decode(byte[] bytes, IEnumerable<string> approvedOrigins)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 2 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out JsonElement version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int parsedVersion)) throw new JsonException();
        return parsedVersion switch
        {
            2 => DecodeV2(root, approvedOrigins),
            1 => DecodeV1(root, approvedOrigins),
            _ => throw new JsonException(),
        };
    }

    private static HostedProvisioningState DecodeV2(JsonElement root, IEnumerable<string> origins)
    {
        string[] fields = ["version", "origin", "requestId", "readCapability", "writeCapability", "readVerifier", "writeVerifier", "phase", "overlayId"];
        RequireFields(root, fields);
        string origin = String(root, "origin"), requestId = String(root, "requestId"), read = String(root, "readCapability"),
            write = String(root, "writeCapability"), readVerifier = String(root, "readVerifier"), writeVerifier = String(root, "writeVerifier");
        string? overlayId = root.GetProperty("overlayId").ValueKind == JsonValueKind.Null ? null : String(root, "overlayId");
        HostedProvisioningPhase phase = ParsePhase(root);
        if (!HostedPublisherConfiguration.Hex(requestId, 64) || !HostedPublisherConfiguration.Hex(read, 64) ||
            !HostedPublisherConfiguration.Hex(write, 64) || read == write || requestId == read || requestId == write ||
            !HostedPublisherConfiguration.Hex(readVerifier, 64) || !HostedPublisherConfiguration.Hex(writeVerifier, 64) ||
            readVerifier == writeVerifier || readVerifier != HostedPublisherConfiguration.VerifierV2(requestId, "read", read) ||
            writeVerifier != HostedPublisherConfiguration.VerifierV2(requestId, "write", write) ||
            (phase == HostedProvisioningPhase.Claiming && overlayId is not null) ||
            (phase == HostedProvisioningPhase.Acknowledged && !HostedPublisherConfiguration.Hex(overlayId, 64))) throw new JsonException();
        _ = HostedPublisherConfiguration.CreateV2(origin, overlayId ?? new string('0', 64), requestId, read, write, origins);
        return new(origin, requestId, read, write, readVerifier, writeVerifier, phase, overlayId);
    }

    private static HostedProvisioningState DecodeV1(JsonElement root, IEnumerable<string> origins)
    {
        string[] fields = ["version", "origin", "slotId", "setupGrant", "requestId", "readCapability", "writeCapability",
            "readVerifier", "writeVerifier", "phase", "paused"];
        RequireFields(root, fields);
        string origin = String(root, "origin"), slotId = String(root, "slotId"), grant = String(root, "setupGrant"),
            requestId = String(root, "requestId"), read = String(root, "readCapability"), write = String(root, "writeCapability"),
            readVerifier = String(root, "readVerifier"), writeVerifier = String(root, "writeVerifier");
        if (!HostedPublisherConfiguration.Hex(slotId, 32) || !HostedPublisherConfiguration.Hex(grant, 64) ||
            !HostedPublisherConfiguration.Hex(requestId, 32) || !HostedPublisherConfiguration.Hex(readVerifier, 64) ||
            !HostedPublisherConfiguration.Hex(writeVerifier, 64) || readVerifier == writeVerifier ||
            readVerifier != HostedPublisherConfiguration.Verifier(slotId, "read", read) ||
            writeVerifier != HostedPublisherConfiguration.Verifier(slotId, "write", write) ||
            root.GetProperty("paused").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException();
        HostedProvisioningPhase phase = ParsePhase(root);
        var configuration = HostedPublisherConfiguration.Create(origin, slotId, read, write, origins);
        return new(origin, requestId, read, write, readVerifier, writeVerifier, phase,
            phase == HostedProvisioningPhase.Acknowledged ? slotId : null, configuration, isPreReleaseVersion1: true,
            legacySetupGrant: grant);
    }

    private static void RequireFields(JsonElement root, string[] fields)
    {
        if (root.EnumerateObject().Count() != fields.Length || fields.Any(field => !root.TryGetProperty(field, out _))) throw new JsonException();
    }
    private static string String(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw new JsonException();
    private static HostedProvisioningPhase ParsePhase(JsonElement root) => root.GetProperty("phase").GetString() switch
    {
        "claiming" => HostedProvisioningPhase.Claiming,
        "acknowledged" => HostedProvisioningPhase.Acknowledged,
        _ => throw new JsonException(),
    };
    public override string ToString() => "Hosted provisioning state (protected)";
}

/// <summary>Separate bounded DPAPI record for an exact recoverable create request.</summary>
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
        catch { throw new InvalidOperationException("Unable to save protected pending overlay state; previous state retained."); }
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
        catch { throw new InvalidOperationException("Unable to load protected pending overlay state."); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task RemovePendingAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { File.Delete(path); }
        catch { throw new InvalidOperationException("Unable to remove protected pending overlay state."); }
    }, cancellationToken);
}
