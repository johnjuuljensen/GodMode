using GodMode.ClientBase.Services.Models;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Manages named credentials (API keys, tokens, etc.) stored encrypted at rest.
/// Credentials are scoped per profile.
/// </summary>
public interface ICredentialService
{
	Task<IReadOnlyList<Credential>> ListCredentialsAsync(string profileName);
	Task<Credential?> GetCredentialAsync(string profileName, string name);
	Task SaveCredentialAsync(string profileName, string name, string type, string plainValue);
	Task DeleteCredentialAsync(string profileName, string name);
	string DecryptValue(string encryptedValue);

	/// <summary>
	/// Resolves a set of credential names to their plaintext values.
	/// Returns a dictionary of name → decrypted value.
	/// </summary>
	Task<Dictionary<string, string>> ResolveCredentialsAsync(string profileName, IEnumerable<string> names);
}
