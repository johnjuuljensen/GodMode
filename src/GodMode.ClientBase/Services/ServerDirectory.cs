using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Builds an <see cref="IServerProvider"/> per registration, fresh on each call, with its token from secure storage.
/// </summary>
public class ServerDirectory(
    IServerRegistryService registry,
    ServerUrlSelector urlSelector,
    ILoggerFactory loggerFactory) : IServerDirectory
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ServerDirectory>();

    public async Task<IReadOnlyList<ServerInfo>> ListAllServersAsync(CancellationToken ct = default)
    {
        var providers = await GetProvidersAsync();
        var lists = await Task.WhenAll(providers.Select(async p =>
        {
            try
            {
                return await p.Provider.ListServersAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing servers of registration {Id} ({Type})", p.Registration.Id, p.Provider.Type);
                return [];
            }
        }));
        var servers = lists.SelectMany(l => l).ToList();
        _logger.LogInformation("Discovered {Count} servers: [{Servers}]", servers.Count,
            string.Join(", ", servers.Select(s => $"{s.Name}({s.State})")));
        return servers;
    }

    public async Task<RelayTarget?> ResolveAsync(string serverId, CancellationToken ct = default) =>
        await FindAsync(serverId) is { } found ? await found.Provider.ResolveAsync(serverId, ct) : null;

    public async Task<bool> StartServerAsync(string serverId)
    {
        if (await FindAsync(serverId) is not { } found) return false;
        await found.Provider.StartServerAsync(serverId);
        return true;
    }

    public async Task<bool> StopServerAsync(string serverId)
    {
        if (await FindAsync(serverId) is not { } found) return false;
        await found.Provider.StopServerAsync(serverId);
        return true;
    }

    public async Task<bool> RemoveServerAsync(string serverId) =>
        await FindAsync(serverId) is { } found && await registry.RemoveServerAsync(found.Registration.Id);

    /// <summary>The registration serving a server. Local registrations are matched by ID before any codespace lookup.</summary>
    private async Task<(ServerRegistration Registration, IServerProvider Provider)?> FindAsync(string serverId)
    {
        foreach (var candidate in (await GetProvidersAsync()).OrderBy(p => p.Provider.Type != ServerTypes.Local))
        {
            try
            {
                if (await candidate.Provider.OwnsAsync(serverId))
                    return candidate;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check registration {Id} for server {ServerId}", candidate.Registration.Id, serverId);
            }
        }
        _logger.LogWarning("Server {ServerId} is not served by any registration", serverId);
        return null;
    }

    private async Task<IReadOnlyList<(ServerRegistration Registration, IServerProvider Provider)>> GetProvidersAsync()
    {
        var result = new List<(ServerRegistration, IServerProvider)>();
        foreach (var registration in await registry.GetServersAsync())
        {
            if (await CreateProviderAsync(registration) is { } provider)
                result.Add((registration, provider));
            else
                _logger.LogWarning("Skipping unusable registration {Id} (type={Type})", registration.Id, registration.Type);
        }
        return result;
    }

    private async Task<IServerProvider?> CreateProviderAsync(ServerRegistration registration)
    {
        var token = await registry.GetAccessTokenAsync(registration.Id);
        return registration.Type switch
        {
            ServerTypes.GitHub when !string.IsNullOrEmpty(token) =>
                new GitHubCodespaceProvider(token, loggerFactory),
            ServerTypes.Local when registration.Urls.Count > 0 && registration.Urls.All(IsHttpUrl) =>
                new LocalFolderProvider(registration, token, urlSelector),
            _ => null,
        };
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
