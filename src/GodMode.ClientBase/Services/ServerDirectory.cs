using GodMode.ClientBase.Abstractions;
using GodMode.ClientBase.Providers;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Models;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.ClientBase.Services;

/// <summary>
/// Builds an <see cref="IServerProvider"/> per registration, fresh on each call, with its token from secure storage.
/// A registration whose token cannot be read is left out as unavailable; the others stay listed and relayable.
/// </summary>
public class ServerDirectory(
    IServerRegistryService registry,
    ServerUrlSelector urlSelector,
    ILoggerFactory loggerFactory) : IServerDirectory
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ServerDirectory>();

    public async Task<IReadOnlyList<ServerInfo>> ListAllServersAsync(CancellationToken ct = default)
    {
        var servers = (await ListByRegistrationAsync(ct)).SelectMany(l => l.Servers ?? []).ToList();
        _logger.LogInformation("Discovered {Count} servers: [{Servers}]", servers.Count,
            string.Join(", ", servers.Select(s => $"{s.Name}({s.State})")));
        return servers;
    }

    public async Task<IReadOnlyList<RegistrationListing>> ListByRegistrationAsync(CancellationToken ct = default)
    {
        var providers = await GetProvidersAsync();
        return await Task.WhenAll(providers.Select(async p =>
        {
            if (p.Provider is null)
                return new RegistrationListing(p.Registration.Id, null);
            try
            {
                return new RegistrationListing(p.Registration.Id, await p.Provider.ListServersAsync(ct));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing servers of registration {Id} ({Type})", p.Registration.Id, p.Provider.Type);
                return new RegistrationListing(p.Registration.Id, null);
            }
        }));
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
        foreach (var (registration, provider) in (await GetProvidersAsync()).OrderBy(p => p.Registration.Type != ServerTypes.Local))
        {
            if (provider is null) continue;
            try
            {
                if (await provider.OwnsAsync(serverId))
                    return (registration, provider);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not check registration {Id} for server {ServerId}", registration.Id, serverId);
            }
        }
        _logger.LogWarning("Server {ServerId} is not served by any registration", serverId);
        return null;
    }

    /// <summary>
    /// A provider per usable registration. One whose token could not be read is kept with a null provider:
    /// it is unavailable (listed as failed, never resolved), and the other registrations are unaffected.
    /// </summary>
    private async Task<IReadOnlyList<(ServerRegistration Registration, IServerProvider? Provider)>> GetProvidersAsync()
    {
        var result = new List<(ServerRegistration, IServerProvider?)>();
        foreach (var registration in await registry.GetServersAsync())
        {
            string? token;
            try
            {
                token = await registry.GetAccessTokenAsync(registration.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read the access token of registration {Id} ({Type}); it is unavailable", registration.Id, registration.Type);
                result.Add((registration, null));
                continue;
            }

            if (CreateProvider(registration, token) is { } provider)
                result.Add((registration, provider));
            else
                _logger.LogWarning("Skipping unusable registration {Id} (type={Type})", registration.Id, registration.Type);
        }
        return result;
    }

    private IServerProvider? CreateProvider(ServerRegistration registration, string? token) => registration.Type switch
    {
        ServerTypes.GitHub when !string.IsNullOrEmpty(token) =>
            new GitHubCodespaceProvider(token, loggerFactory),
        ServerTypes.Local when registration.Urls.Count > 0 && registration.Urls.All(IsHttpUrl) =>
            new LocalFolderProvider(registration, token, urlSelector),
        _ => null,
    };

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
