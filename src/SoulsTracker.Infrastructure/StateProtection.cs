using System.Security.Cryptography;

namespace SoulsTracker.Infrastructure;

public interface IStateSecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CurrentUserDpapiSecretProtector : IStateSecretProtector
{
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext ?? throw new ArgumentNullException(nameof(plaintext)), optionalEntropy: null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext ?? throw new ArgumentNullException(nameof(ciphertext)), optionalEntropy: null, DataProtectionScope.CurrentUser);
}
