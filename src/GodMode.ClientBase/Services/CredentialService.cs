using GodMode.ClientBase.Services.Models;
using System.Collections.Concurrent;
using System.Text.Json;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Stores credentials encrypted per-profile in ~/.godmode/credentials-{profileName}.json.
/// </summary>
public class CredentialService : ICredentialService
{
	private readonly string _appDataPath;
	private readonly EncryptionHelper _encryption;
	private readonly ConcurrentDictionary<string, List<Credential>> _cache = new();

	public CredentialService(string appDataPath, EncryptionHelper encryption)
	{
		_appDataPath = appDataPath;
		_encryption = encryption;
	}

	public async Task<IReadOnlyList<Credential>> ListCredentialsAsync(string profileName)
	{
		var creds = await LoadAsync(profileName);
		return creds.AsReadOnly();
	}

	public async Task<Credential?> GetCredentialAsync(string profileName, string name)
	{
		var creds = await LoadAsync(profileName);
		return creds.FirstOrDefault(c => c.Name == name);
	}

	public async Task SaveCredentialAsync(string profileName, string name, string type, string plainValue)
	{
		var creds = await LoadAsync(profileName);
		var encrypted = _encryption.Encrypt(plainValue);

		creds.RemoveAll(c => c.Name == name);
		creds.Add(new Credential(name, type, encrypted));
		await SaveAsync(profileName, creds);
	}

	public async Task DeleteCredentialAsync(string profileName, string name)
	{
		var creds = await LoadAsync(profileName);
		if (creds.RemoveAll(c => c.Name == name) > 0)
			await SaveAsync(profileName, creds);
	}

	public string DecryptValue(string encryptedValue) => _encryption.Decrypt(encryptedValue);

	public async Task<Dictionary<string, string>> ResolveCredentialsAsync(string profileName, IEnumerable<string> names)
	{
		var creds = await LoadAsync(profileName);
		var result = new Dictionary<string, string>();

		foreach (var name in names)
		{
			var cred = creds.FirstOrDefault(c => c.Name == name);
			if (cred != null)
				result[name] = _encryption.Decrypt(cred.EncryptedValue);
		}

		return result;
	}

	private string GetFilePath(string profileName) =>
		Path.Combine(_appDataPath, $"credentials-{profileName}.json");

	private async Task<List<Credential>> LoadAsync(string profileName)
	{
		if (_cache.TryGetValue(profileName, out var cached))
			return cached;

		var filePath = GetFilePath(profileName);

		// Migration: if profile-specific file doesn't exist but old global file does, migrate
		if (!File.Exists(filePath))
		{
			var legacyPath = Path.Combine(_appDataPath, "credentials.json");
			if (File.Exists(legacyPath))
			{
				try
				{
					// Copy legacy file to first profile that accesses it
					File.Copy(legacyPath, filePath);
				}
				catch { /* Non-critical */ }
			}
		}

		if (!File.Exists(filePath))
		{
			var empty = new List<Credential>();
			_cache[profileName] = empty;
			return empty;
		}

		try
		{
			var json = await File.ReadAllTextAsync(filePath);
			var creds = JsonSerializer.Deserialize<List<Credential>>(json) ?? new();
			_cache[profileName] = creds;
			return creds;
		}
		catch
		{
			var empty = new List<Credential>();
			_cache[profileName] = empty;
			return empty;
		}
	}

	private async Task SaveAsync(string profileName, List<Credential> creds)
	{
		_cache[profileName] = creds;
		var json = JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true });
		await File.WriteAllTextAsync(GetFilePath(profileName), json);
	}
}
