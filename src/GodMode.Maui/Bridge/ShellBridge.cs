using System.Collections.Concurrent;
using GodMode.ClientBase.Services;
using GodMode.ClientBase.Services.Models;
using GodMode.Maui.Voice;
using GodMode.Voice;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalR.Proxy;

namespace GodMode.Maui.Bridge;

/// <summary>
/// The shell's side of the React ↔ host API (<see cref="ShellMessageTypes"/>): relay info, server management,
/// the window the page is in, and the servers.changed event. Server tokens go into secure storage here and are
/// never sent back.
/// Each window's page gets its own (#340): the main window's (<see cref="Profile"/> null) and one per profile window.
/// A page that replaces its window's (the activity was recreated, from a notification tap say) detaches the one
/// before it, which lets go of the process-wide events through <see cref="Dispose"/>. The main window's bridge takes
/// the app-wide events (notification taps, the network), and servers.changed goes to every window's page.
/// </summary>
public sealed class ShellBridge : IDisposable
{
    /// <summary>The bridge attached in each window, by its profile ("" for the main window), without case.</summary>
    private static readonly ConcurrentDictionary<string, ShellBridge> Attached = new(StringComparer.OrdinalIgnoreCase);
    private static int _created;

    private readonly int _number = Interlocked.Increment(ref _created);
    private readonly HybridWebView _webView;
    private readonly HostBridge _bridge;
    private readonly LocalServer _relay;
    private readonly IServerDirectory _directory;
    private readonly IServerRegistryService _registry;
    private readonly VoiceHost _voice;
    private readonly ILogger _logger;
    private IDisposable? _voicePage;

    private ShellBridge(HybridWebView webView, string? profile, IServiceProvider services, ILogger logger)
    {
        _webView = webView;
        Profile = profile;
        _bridge = new HostBridge(webView, logger);
        _relay = services.GetRequiredService<LocalServer>();
        _directory = services.GetRequiredService<IServerDirectory>();
        _registry = services.GetRequiredService<IServerRegistryService>();
        _voice = services.GetRequiredService<VoiceHost>();
        _logger = logger;
    }

    /// <summary>The profile the page's window is locked to, by name across every server; null in the main window.</summary>
    public string? Profile { get; }

    private bool IsMain => Profile is null;

    /// <summary>
    /// Connects the WebView's raw-message channel to the shell API, for a page in the main window (no profile) or in a
    /// profile's, detaching the bridge attached in that window before.
    /// </summary>
    public static ShellBridge Attach(HybridWebView webView, string? profile, IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<ShellBridge>();
        var shell = new ShellBridge(webView, profile, services, logger);
        ShellBridge? before = null;
        Attached.AddOrUpdate(profile ?? "", shell, (_, attached) => { before = attached; return shell; });
        before?.Dispose();
        shell.Register();
        shell._voicePage = shell._voice.Attach(shell._bridge.Send);
        if (shell.IsMain)
        {
            // On Windows, should taps come, one belongs in the window holding the item's profile (its bridge's
            // Profile), and in the main window only when no window holds it. Android has the one window
            PendingAttentionLink.Arrived += shell.OnAttentionArrived;
            Connectivity.Current.ConnectivityChanged += shell.OnNetworkChanged;
        }
        logger.LogInformation("Shell bridge #{Number} attached for {Window}; windows: {Windows}; bridges listening for notification taps: {Listening}",
            shell._number, profile ?? "the main window", Attached.Count, PendingAttentionLink.Listening);
        return shell;
    }

    /// <summary>
    /// Lets go of the process-wide events, which would otherwise keep this bridge, its WebView and page alive: its page
    /// was replaced, or its window closed.
    /// </summary>
    public void Dispose()
    {
        Attached.TryRemove(new KeyValuePair<string, ShellBridge>(Profile ?? "", this));
        _voicePage?.Dispose();
        PendingAttentionLink.Arrived -= OnAttentionArrived;
        Connectivity.Current.ConnectivityChanged -= OnNetworkChanged;
        _logger.LogInformation("Shell bridge #{Number} detached ({Window})", _number, Profile ?? "the main window");
    }

    /// <summary>An event for every window's page: the server list is the app's, whichever window changed it.</summary>
    private static void Broadcast(string type)
    {
        foreach (var shell in Attached.Values) shell._bridge.Send(type);
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
            MainPage.OpenDevTools(_webView);
            return Task.FromResult(true);
        });
        _bridge.Handle(ShellMessageTypes.WindowInfo, () => Task.FromResult(new WindowInfo(Profile, AppWindows.CanOpen)));
        _bridge.Handle<ProfilePayload, bool>(ShellMessageTypes.WindowOpenProfile, p =>
        {
            if (string.IsNullOrWhiteSpace(p.Profile)) throw new ArgumentException("Name the profile to open");
            AppWindows.OpenProfile(p.Profile.Trim());
            return Task.FromResult(true);
        });

        _bridge.Handle(ShellMessageTypes.VoiceState, () => Task.FromResult(_voice.Status));
        _bridge.Handle(ShellMessageTypes.VoiceStart, _voice.StartAsync);
        _bridge.Handle(ShellMessageTypes.VoiceStop, _voice.StopAsync);
        _bridge.Handle(ShellMessageTypes.VoiceMicOpen, _voice.OpenMicAsync);
        _bridge.Handle(ShellMessageTypes.VoiceMicClose, _voice.CloseMicAsync);
        _bridge.Handle(ShellMessageTypes.VoiceSettingsGet, _voice.Settings.GetViewAsync);
        _bridge.Handle<VoiceSettingsUpdate, VoiceSettingsView>(ShellMessageTypes.VoiceSettingsSet, _voice.UpdateSettingsAsync);
        _bridge.Handle(ShellMessageTypes.VoiceDevices, () => Task.Run(VoiceAudio.Devices));
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
        Broadcast(ShellMessageTypes.ServersChanged);
        _voice.ServersChanged();
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
                Broadcast(ShellMessageTypes.ServersChanged);
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
        _voice.NetworkChanged();
        Broadcast(ShellMessageTypes.ServersChanged);
    }
}
