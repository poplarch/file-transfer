using System.Security.Cryptography;
using System.Text;

namespace FileTransfer.App.Security;

/// <summary>
/// Encrypts and decrypts sensitive credentials for local storage.
/// Uses Windows DPAPI (CurrentUser) on Windows, and an AES-GCM fallback on macOS/Linux for local development.
/// </summary>
public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FileTransfer.Relay.Credential.v1");

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return "DPAPI:" + Convert.ToBase64String(protectedBytes);
        }

        // Local dev fallback on macOS/Linux so debugging works without Windows DPAPI.
        return "DEV:" + Convert.ToBase64String(EncryptAesGcm(plainBytes));
    }

    public static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return string.Empty;
        }

        try
        {
            if (cipherText.StartsWith("DPAPI:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows())
                {
                    return string.Empty;
                }

                var data = Convert.FromBase64String(cipherText["DPAPI:".Length..]);
                var plainBytes = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }

            if (cipherText.StartsWith("DEV:", StringComparison.Ordinal))
            {
                var data = Convert.FromBase64String(cipherText["DEV:".Length..]);
                var plainBytes = DecryptAesGcm(data);
                return Encoding.UTF8.GetString(plainBytes);
            }
        }
        catch
        {
            // Return empty on decryption failure (e.g. copied from another user/machine).
        }

        return string.Empty;
    }

    private static byte[] DeriveDevKey()
    {
        var material = $"{Environment.UserName}@{Environment.MachineName}:FileTransferDevKey";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }

    private static byte[] EncryptAesGcm(byte[] plainBytes)
    {
        var key = DeriveDevKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plainBytes.Length];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag, Entropy);

        var combined = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, combined, nonce.Length + tag.Length, cipher.Length);
        return combined;
    }

    private static byte[] DecryptAesGcm(byte[] combined)
    {
        if (combined.Length < 28)
        {
            return Array.Empty<byte>();
        }

        var key = DeriveDevKey();
        var nonce = combined[..12];
        var tag = combined[12..28];
        var cipher = combined[28..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain, Entropy);
        return plain;
    }
}
