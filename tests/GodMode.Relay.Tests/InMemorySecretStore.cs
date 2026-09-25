using System.Collections.Concurrent;
using GodMode.ClientBase.Services;

namespace GodMode.Relay.Tests;

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte> _unreadable = new();

    public ConcurrentDictionary<string, string> Values { get; } = new();

    /// <summary>Reading this key throws from now on, as a secure store whose entry is damaged does.</summary>
    public void MakeUnreadable(string key) => _unreadable[key] = 0;

    public Task<string?> GetAsync(string key) => _unreadable.ContainsKey(key)
        ? throw new InvalidOperationException($"secure storage could not read {key}")
        : Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public bool Remove(string key) => Values.TryRemove(key, out _);
}
