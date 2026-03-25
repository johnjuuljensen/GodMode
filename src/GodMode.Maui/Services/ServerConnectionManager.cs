using System.Collections.Concurrent;
using GodMode.Shared;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace GodMode.Maui.Services;

/// <summary>
/// Manages outbound SignalR connections to remote GodMode.Server instances.
/// Provides raw HubConnection access for the proxy filter, and forwards
/// remote callbacks to local React clients via group-based routing.
/// </summary>
public class ServerConnectionManager
{
    private readonly ConcurrentDictionary<string, ManagedServer> _servers = new();
    private readonly IHubContext<Hubs.GodModeLocalHub> _hubContext;
    private readonly string _registryPath;

    public ServerConnectionManager(IHubContext<Hubs.GodModeLocalHub> hubContext)
    {
        _hubContext = hubContext;
        _registryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GodMode", "servers.json");
    }

    public HubConnection? GetRemoteConnection(string serverId) =>
        _servers.TryGetValue(serverId, out var server) && server.IsConnected
            ? server.RemoteConnection
            : null;

    public ServerInfo[] ListServers() =>
        _servers.Values.Select(s => s.ToServerInfo()).ToArray();

    public ServerInfo AddServer(AddServerRequest request)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var server = new ManagedServer(id, request.DisplayName, request.Url, request.AccessToken);
        _servers[id] = server;
        SaveRegistry();
        return server.ToServerInfo();
    }

    public void UpdateServer(string serverId, AddServerRequest request)
    {
        if (!_servers.TryGetValue(serverId, out var existing))
            throw new KeyNotFoundException($"Server '{serverId}' not found");

        if (existing.IsConnected)
            throw new InvalidOperationException("Disconnect before updating");

        _servers[serverId] = new ManagedServer(serverId, request.DisplayName, request.Url, request.AccessToken);
        SaveRegistry();
    }

    public async Task RemoveServer(string serverId)
    {
        if (_servers.TryRemove(serverId, out var server))
        {
            await server.DisconnectAsync();
            SaveRegistry();
        }
    }

    public async Task ConnectServer(string serverId)
    {
        if (!_servers.TryGetValue(serverId, out var server))
            throw new KeyNotFoundException($"Server '{serverId}' not found");

        await server.ConnectAsync(_hubContext, serverId);
    }

    public async Task DisconnectServer(string serverId)
    {
        if (_servers.TryGetValue(serverId, out var server))
            await server.DisconnectAsync();
    }

    public void LoadRegistry()
    {
        if (!File.Exists(_registryPath)) return;

        try
        {
            var json = File.ReadAllText(_registryPath);
            var entries = System.Text.Json.JsonSerializer.Deserialize<ServerEntry[]>(json, JsonDefaults.Options);
            if (entries is null) return;

            foreach (var entry in entries)
                _servers[entry.Id] = new ManagedServer(entry.Id, entry.DisplayName, entry.Url, entry.AccessToken);
        }
        catch { /* corrupt file, start fresh */ }
    }

    private void SaveRegistry()
    {
        var dir = Path.GetDirectoryName(_registryPath)!;
        Directory.CreateDirectory(dir);

        var entries = _servers.Values.Select(s => new ServerEntry(s.Id, s.DisplayName, s.Url, s.AccessToken)).ToArray();
        var json = System.Text.Json.JsonSerializer.Serialize(entries, JsonDefaults.Options);
        File.WriteAllText(_registryPath, json);
    }

    private record ServerEntry(string Id, string DisplayName, string Url, string? AccessToken);
}

internal class ManagedServer
{
    public string Id { get; }
    public string DisplayName { get; }
    public string Url { get; }
    public string? AccessToken { get; }

    public HubConnection? RemoteConnection { get; private set; }
    public bool IsConnected => RemoteConnection?.State == HubConnectionState.Connected;

    public ManagedServer(string id, string displayName, string url, string? accessToken)
    {
        Id = id;
        DisplayName = displayName;
        Url = url;
        AccessToken = accessToken;
    }

    public async Task ConnectAsync(IHubContext<Hubs.GodModeLocalHub> hubContext, string serverId)
    {
        if (IsConnected) return;

        var hubUrl = Url.TrimEnd('/') + "/hubs/projects";
        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrEmpty(AccessToken))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(AccessToken);
            })
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                var defaults = JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            });

        RemoteConnection = builder.Build();

        // Forward all IProjectHubClient callbacks to local React clients in this server's group
        foreach (var method in typeof(IProjectHubClient).GetMethods())
        {
            var methodName = method.Name;
            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            RemoteConnection.On(methodName, paramTypes, args =>
                hubContext.Clients.Group($"server-{serverId}").SendCoreAsync(methodName, args));
        }

        await RemoteConnection.StartAsync();
    }

    public async Task DisconnectAsync()
    {
        if (RemoteConnection is not null)
        {
            await RemoteConnection.DisposeAsync();
            RemoteConnection = null;
        }
    }

    public ServerInfo ToServerInfo() => new(Id, DisplayName, Url, IsConnected ? "connected" : "disconnected");
}
