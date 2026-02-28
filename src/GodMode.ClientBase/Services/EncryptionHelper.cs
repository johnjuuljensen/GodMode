using System.Security.Cryptography;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Shared AES encryption utility for encrypting secrets at rest.
/// Used by ProfileService and CredentialService.
/// </summary>
public class EncryptionHelper
{
	private readonly byte[] _key;

	public EncryptionHelper(string appDataPath)
	{
		_key = GetOrCreateKey(Path.Combine(appDataPath, ".encryption_key"));
	}

	public string Encrypt(string plainText)
	{
		using var aes = Aes.Create();
		aes.Key = _key;
		aes.GenerateIV();

		using var encryptor = aes.CreateEncryptor();
		using var ms = new MemoryStream();

		ms.Write(aes.IV, 0, aes.IV.Length);

		using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
		using (var sw = new StreamWriter(cs))
		{
			sw.Write(plainText);
		}

		return Convert.ToBase64String(ms.ToArray());
	}

	public string Decrypt(string encryptedText)
	{
		try
		{
			var encryptedBytes = Convert.FromBase64String(encryptedText);

			using var aes = Aes.Create();
			aes.Key = _key;

			var iv = new byte[aes.BlockSize / 8];
			Array.Copy(encryptedBytes, 0, iv, 0, iv.Length);
			aes.IV = iv;

			using var decryptor = aes.CreateDecryptor();
			using var ms = new MemoryStream(encryptedBytes, iv.Length, encryptedBytes.Length - iv.Length);
			using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
			using var sr = new StreamReader(cs);

			return sr.ReadToEnd();
		}
		catch
		{
			// If decryption fails, assume it's already decrypted or invalid
			return encryptedText;
		}
	}

	private static byte[] GetOrCreateKey(string keyPath)
	{
		if (File.Exists(keyPath))
			return File.ReadAllBytes(keyPath);

		using var aes = Aes.Create();
		aes.GenerateKey();
		var key = aes.Key;

		File.WriteAllBytes(keyPath, key);
		return key;
	}
}
