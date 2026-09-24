namespace GodMode.ClientBase.Services;

/// <summary>
/// Platform secure storage for server access tokens (Android Keystore, Windows DPAPI, Apple Keychain).
/// The MAUI host implements it with <c>SecureStorage</c>. Values never reach a plain file.
/// </summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    bool Remove(string key);
}
