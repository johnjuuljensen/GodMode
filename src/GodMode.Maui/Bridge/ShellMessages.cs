namespace GodMode.Maui.Bridge;

/// <summary>
/// Message types the React app (services/hostBridge.ts) exchanges with the shell. Keep the two in sync.
/// No response carries a server's access token: it goes from React to the shell in servers.add and never back.
/// </summary>
public static class ShellMessageTypes
{
    /// <summary>Request → <see cref="RelayInfo"/>.</summary>
    public const string RelayInfo = "relay.info";

    /// <summary>Request → ServerInfo[].</summary>
    public const string ServersList = "servers.list";

    /// <summary>Request <see cref="AddServerPayload"/> → <see cref="AddServerResult"/>.</summary>
    public const string ServersAdd = "servers.add";

    /// <summary>Request <see cref="ServerIdPayload"/>; removes the registration serving the server.</summary>
    public const string ServersRemove = "servers.remove";

    /// <summary>Request <see cref="ServerIdPayload"/>.</summary>
    public const string ServersStart = "servers.start";

    /// <summary>Request <see cref="ServerIdPayload"/>.</summary>
    public const string ServersStop = "servers.stop";

    /// <summary>Request; opens the WebView's developer tools where the platform has them (Windows).</summary>
    public const string OpenDevTools = "host.openDevTools";

    /// <summary>Event from the shell: the server list or a server's state changed.</summary>
    public const string ServersChanged = "servers.changed";

    /// <summary>Request → <see cref="AttentionLinkPayload"/>, or null: the item a notification tap opened, once.</summary>
    public const string AttentionTake = "attention.take";

    /// <summary>Event from the shell: a notification was tapped; attention.take has its item.</summary>
    public const string AttentionOpen = "attention.open";
}

/// <summary>The relay's base URL and the per-launch secret it requires (as the access_token query parameter).</summary>
public sealed record RelayInfo(string BaseUrl, string Secret);

/// <summary>A server to register. A local server has one or more URLs, in order of preference.</summary>
public sealed record AddServerPayload(
    string Type,
    string? DisplayName,
    IReadOnlyList<string>? Urls,
    string? Username,
    string? AccessToken);

public sealed record AddServerResult(string Id);

public sealed record ServerIdPayload(string ServerId);

/// <summary>An attention item to open in the inbox: a project on a server, both IDs as the server gave them.</summary>
public sealed record AttentionLinkPayload(string ServerId, string ProjectId);
