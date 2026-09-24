using System.Collections.Concurrent;
using GodMode.ClientBase.Services;

namespace GodMode.Relay.Tests;

internal sealed class InMemorySecretStore : ISecretStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();

    public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public bool Remove(string key) => Values.TryRemove(key, out _);
}
