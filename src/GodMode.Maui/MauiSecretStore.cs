using GodMode.ClientBase.Services;

namespace GodMode.Maui;

/// <summary>
/// Server tokens in MAUI SecureStorage: Android Keystore-backed encrypted preferences,
/// DPAPI on Windows, the Keychain on Apple platforms.
/// </summary>
public sealed class MauiSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);
    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);
    public bool Remove(string key) => SecureStorage.Default.Remove(key);
}
