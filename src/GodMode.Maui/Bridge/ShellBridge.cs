using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.Maui.Bridge;

/// <summary>
/// The shell's side of the React ↔ host API (<see cref="ShellMessageTypes"/>): relay info, server management,
/// and the servers.changed event. Server tokens go into secure storage here and are never sent back.
/// One is attached at a time: each page gets its own, and the page it replaces (the activity was recreated,
/// from a notification tap say) lets go of the process-wide events through <see cref="Dispose"/>.
/// </summary>
public sealed class ShellBridge : IDisposable
{
    private static ShellBridge? _attached;
    private static int _created;

    private readonly int _number = Interlocked.Increment(ref _created);
    private readonly HostBridge _bridge;
    private readonly LocalServer _relay;
    private readonly IServerDirectory _directory;
    private readonly IServerRegistryService _registry;
    private readonly ILogger _logger;

    private ShellBridge(HostBridge bridge, IServiceProvider services, ILogger logger)
    {
        _bridge = bridge;
        _relay = services.GetRequiredService<LocalServer>();
        _directory = services.GetRequiredService<IServerDirectory>();
        _registry = services.GetRequiredService<IServerRegistryService>();
        _logger = logger;
    }

    /// <summary>Connects the WebView's raw-message channel to the shell API, detaching the bridge attached before.</summary>
    public static ShellBridge Attach(HybridWebView webView, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<ShellBridge>();
        var shell = new ShellBridge(new HostBridge(webView, logger), services, logger);
        Interlocked.Exchange(ref _attached, shell)?.Dispose();
        shell.Register();
        PendingAttentionLink.Arrived += shell.OnAttentionArrived;
        Connectivity.Current.ConnectivityChanged += shell.OnNetworkChanged;
        logger.LogInformation("Shell bridge #{Number} attached; bridges listening for notification taps: {Listening}",
            shell._number, PendingAttentionLink.Listening);
        return shell;
    }

    /// <summary>Lets go of the process-wide events, which would otherwise keep this bridge, its WebView and page alive.</summary>
    public void Dispose()
    {
        PendingAttentionLink.Arrived -= OnAttentionArrived;
        Connectivity.Current.ConnectivityChanged -= OnNetworkChanged;
        _logger.LogInformation("Shell bridge #{Number} detached", _number);
    }

    private void Register()
    {
        _bridge.Handle(ShellMessageTypes.RelayInfo, () => Task.FromResult(new RelayInfo(_relay.BaseUrl, _relay.Secret)));
        _bridge.Handle(ShellMessageTypes.ServersList, () => _directory.ListAllServersAsync());
        _bridge.Handle<AddServerPayload, AddServerResult>(ShellMessageTypes.ServersAdd, AddServerAsync);
        _bridge.Handle<ServerIdPayload, bool>(ShellMessageTypes.ServersRemove, async p =>
            await _directory.RemoveServerAsync(p.ServerId) ? Changed(true) : throw NotFound(p));
        _bridge.Handle<ServerIdPayload, bool>(ShellMessageTypes.ServersStart, async p =>
            await _directory.StartServerAsync(p.ServerId) ? Polling(p.ServerId) : throw NotFound(p));
        _bridge.Handle<ServerIdPayload, bool>(ShellMessageTypes.ServersStop, async p =>
            await _directory.StopServerAsync(p.ServerId) ? Polling(p.ServerId) : throw NotFound(p));
        _bridge.Handle(ShellMessageTypes.AttentionTake, () => Task.FromResult(
            PendingAttentionLink.Take() is { } link ? new AttentionLinkPayload(link.ServerId, link.ProjectId) : null));
        _bridge.Handle(ShellMessageTypes.OpenDevTools, () =>
        {
            MainPage.OpenDevTools();
            return Task.FromResult(true);
        });
    }

    private async Task<AddServerResult> AddServerAsync(AddServerPayload p)
    {
        var urls = p.Urls ?? [];
        if (p.Type == ServerTypes.Local && urls.Count == 0)
            throw new ArgumentException("A local server needs at least one URL");
        if (p.Type == ServerTypes.GitHub && (string.IsNullOrWhiteSpace(p.Username) || string.IsNullOrWhiteSpace(p.AccessToken)))
            throw new ArgumentException("A GitHub account needs a username and a token");

        var added = await _registry.AddServerAsync(new ServerRegistration
        {
            Type = p.Type,
            Urls = p.Type == ServerTypes.Local ? urls : [],
            Username = p.Username,
            DisplayName = p.DisplayName,
        }, p.AccessToken);
        _logger.LogInformation("Registered {Type} server {Id} ({Name})", added.Type, added.Id, added.DisplayName);
        return Changed(new AddServerResult(added.Id));
    }

    private static KeyNotFoundException NotFound(ServerIdPayload p) => new($"Server not found: {p.ServerId}");

    private T Changed<T>(T result)
    {
        _bridge.Send(ShellMessageTypes.ServersChanged);
#if ANDROID
        AttentionService.Refresh();
#endif
        return result;
    }

    /// <summary>After a start or stop, reports the server's state until it settles (a codespace takes a while).</summary>
    private bool Polling(string serverId)
    {
        _ = PollServerStateAsync(serverId);
        return Changed(true);
    }

    private async Task PollServerStateAsync(string serverId)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                var server = (await _directory.ListAllServersAsync()).FirstOrDefault(s => s.Id == serverId);
                _bridge.Send(ShellMessageTypes.ServersChanged);
                if (server?.State is null or ServerState.Running or ServerState.Stopped)
                    return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll error for {ServerId}", serverId);
                return;
            }
        }
    }

    private void OnAttentionArrived()
    {
        _logger.LogInformation("Notification tap: bridge #{Number} tells React", _number);
        _bridge.Send(ShellMessageTypes.AttentionOpen);
    }

    /// <summary>A server's best URL may have changed: drop the relays so each reconnect picks it afresh.</summary>
    private void OnNetworkChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        _logger.LogInformation("Network changed ({Access}); bridge #{Number} drops the relays", e.NetworkAccess, _number);
        _relay.DropAllRelays();
        _bridge.Send(ShellMessageTypes.ServersChanged);
    }
}
