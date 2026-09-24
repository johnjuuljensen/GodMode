using System.Collections.Concurrent;
using GodMode.ClientBase.Services;
using GodMode.Shared.Models;
using SignalR.Proxy;

namespace GodMode.Relay.Tests;

/// <summary>A directory whose listing of a registration can be made to fail once, as a GitHub API error does.</summary>
internal sealed class FlakyDirectory(IServerDirectory inner) : IServerDirectory
{
    private readonly ConcurrentDictionary<string, byte> _failNext = new();

    /// <summary>The next listing reports this registration as failed (<see cref="RegistrationListing.Servers"/> null).</summary>
    public void FailOnce(string registrationId) => _failNext[registrationId] = 0;

    public async Task<IReadOnlyList<RegistrationListing>> ListByRegistrationAsync(CancellationToken ct = default) =>
        [.. (await inner.ListByRegistrationAsync(ct))
            .Select(l => _failNext.TryRemove(l.RegistrationId, out _) ? l with { Servers = null } : l)];

    public Task<IReadOnlyList<ServerInfo>> ListAllServersAsync(CancellationToken ct = default) => inner.ListAllServersAsync(ct);
    public Task<RelayTarget?> ResolveAsync(string serverId, CancellationToken ct = default) => inner.ResolveAsync(serverId, ct);
    public Task<bool> StartServerAsync(string serverId) => inner.StartServerAsync(serverId);
    public Task<bool> StopServerAsync(string serverId) => inner.StopServerAsync(serverId);
    public Task<bool> RemoveServerAsync(string serverId) => inner.RemoveServerAsync(serverId);
}
