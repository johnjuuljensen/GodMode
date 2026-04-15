using GodMode.Vault.Services;
using Microsoft.Extensions.Configuration;

namespace GodMode.Vault.Tests;

/// <summary>
/// Tests encryption round-trip, per-user key isolation, and tamper detection.
/// </summary>
public class EncryptionTests
{
    private static EncryptionService CreateService(string? masterSecret = null)
    {
        masterSecret ??= EncryptionService.GenerateMasterSecret();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vault:MasterSecret"] = masterSecret })
            .Build();
        return new EncryptionService(config);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrips()
    {
        var svc = CreateService();
        var plaintext = "hello world"u8.ToArray();

        var encrypted = svc.Encrypt(plaintext, "google:user1");
        var decrypted = svc.Decrypt(encrypted, "google:user1");

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_BinaryData_RoundTrips()
    {
        var svc = CreateService();
        var binary = new byte[1024];
        Random.Shared.NextBytes(binary);

        var encrypted = svc.Encrypt(binary, "github:42");
        var decrypted = svc.Decrypt(encrypted, "github:42");

        Assert.Equal(binary, decrypted);
    }

    [Fact]
    public void EncryptDecrypt_EmptyPayload_RoundTrips()
    {
        var svc = CreateService();
        var empty = Array.Empty<byte>();

        var encrypted = svc.Encrypt(empty, "google:user1");
        var decrypted = svc.Decrypt(encrypted, "google:user1");

        Assert.Equal(empty, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertexts_ForSamePlaintext()
    {
        var svc = CreateService();
        var plaintext = "same data"u8.ToArray();

        var enc1 = svc.Encrypt(plaintext, "google:user1");
        var enc2 = svc.Encrypt(plaintext, "google:user1");

        // Different random nonces → different ciphertexts
        Assert.NotEqual(enc1, enc2);
    }

    // --- Per-user key isolation ---

    [Fact]
    public void Decrypt_WrongUser_Throws()
    {
        var svc = CreateService();
        var encrypted = svc.Encrypt("secret"u8.ToArray(), "google:userA");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => svc.Decrypt(encrypted, "google:userB"));
    }

    [Fact]
    public void Decrypt_DifferentMasterSecret_Throws()
    {
        var svc1 = CreateService();
        var svc2 = CreateService(); // different generated master secret

        var encrypted = svc1.Encrypt("secret"u8.ToArray(), "google:user1");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => svc2.Decrypt(encrypted, "google:user1"));
    }

    // --- Tamper detection ---

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var svc = CreateService();
        var encrypted = svc.Encrypt("secret"u8.ToArray(), "google:user1");

        // Flip a byte in the ciphertext portion (after nonce + tag)
        encrypted[^1] ^= 0xFF;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => svc.Decrypt(encrypted, "google:user1"));
    }

    [Fact]
    public void Decrypt_TruncatedData_Throws()
    {
        var svc = CreateService();

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => svc.Decrypt(new byte[10], "google:user1"));
    }

    // --- Master secret validation ---

    [Fact]
    public void Constructor_NoMasterSecret_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vault:MasterSecret"] = null })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new EncryptionService(config));
    }

    [Fact]
    public void GenerateMasterSecret_ReturnsValidBase64()
    {
        var secret = EncryptionService.GenerateMasterSecret();
        var bytes = Convert.FromBase64String(secret);
        Assert.Equal(32, bytes.Length);
    }
}
