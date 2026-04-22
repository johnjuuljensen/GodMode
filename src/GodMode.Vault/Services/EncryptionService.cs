using System.Security.Cryptography;

namespace GodMode.Vault.Services;

/// <summary>
/// Envelope encryption using HKDF(VMS, user_sub) for key derivation and AES-256-GCM for encryption.
/// </summary>
public sealed class EncryptionService
{
    private const int NonceSize = 12;  // AES-GCM standard
    private const int TagSize = 16;    // AES-GCM standard
    private const int KeySize = 32;    // AES-256

    private readonly byte[] _vaultMasterSecret;

    public EncryptionService(IConfiguration configuration)
    {
        var vms = configuration["Vault:MasterSecret"]
            ?? Environment.GetEnvironmentVariable("VAULT_MASTER_SECRET")
            ?? throw new InvalidOperationException(
                "Vault master secret not configured. Set Vault:MasterSecret or VAULT_MASTER_SECRET env var.");

        _vaultMasterSecret = Convert.FromBase64String(vms);
    }

    public byte[] Encrypt(byte[] plaintext, string userSub)
    {
        var kek = DeriveKey(userSub);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(kek, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Wire format: [nonce (12)] [tag (16)] [ciphertext (N)]
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        ciphertext.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    public byte[] Decrypt(byte[] encrypted, string userSub)
    {
        if (encrypted.Length < NonceSize + TagSize)
            throw new CryptographicException("Encrypted data too short.");

        var kek = DeriveKey(userSub);
        var nonce = encrypted.AsSpan(0, NonceSize);
        var tag = encrypted.AsSpan(NonceSize, TagSize);
        var ciphertext = encrypted.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(kek, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private byte[] DeriveKey(string userSub)
    {
        var info = System.Text.Encoding.UTF8.GetBytes("godmode-vault");
        var sub = System.Text.Encoding.UTF8.GetBytes(userSub);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, _vaultMasterSecret, KeySize, sub, info);
    }

    /// <summary>Generate a new random vault master secret (base64). Utility for setup.</summary>
    public static string GenerateMasterSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
