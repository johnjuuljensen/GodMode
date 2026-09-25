namespace GodMode.Server.Services;

/// <summary>
/// The URL a project's claude calls this server's MCP endpoint on (the <c>url</c> of GodMode's entry
/// in its MCP config), picked from the addresses the server listens on. claude runs on this machine,
/// so it takes, in order: a loopback binding; a wildcard binding (<c>+</c>, <c>*</c>, <c>0.0.0.0</c>,
/// <c>[::]</c>) reached on 127.0.0.1; else the one address bound (a Tailscale or LAN IP only),
/// which this machine reaches too. Kestrel binds any host name but localhost to every address, so
/// such a name counts as a wildcard. http before https: claude need not trust the server's
/// certificate then. A port 0 binding is skipped: only the address it got is reachable.
/// </summary>
public static class McpEndpointUrl
{
    /// <summary>Where the endpoint is mapped (Program.cs).</summary>
    public const string Path = "/mcp";

    /// <summary>What claude is given when nothing is bound or configured (the server's default binding).</summary>
    public const string Default = "http://127.0.0.1:31337" + Path;

    public static string From(IEnumerable<string> addresses) =>
        addresses
            .Select(Reachable)
            .OfType<(Uri Url, int Rank)>()
            .OrderBy(candidate => candidate.Rank)
            .Select(candidate => candidate.Url.GetLeftPart(UriPartial.Authority) + Path)
            .FirstOrDefault()
        ?? Default;

    /// <summary>The URL this machine reaches <paramref name="address"/> on, and how much to prefer it (lower first).</summary>
    private static (Uri Url, int Rank)? Reachable(string address)
    {
        // "+" and "*" are Kestrel's any-address hosts, not host names Uri parses
        var parsed = address.Trim().Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0");
        if (!Uri.TryCreate(parsed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Port is 0 or -1)
            return null;

        var (host, rank) = uri.HostNameType switch
        {
            _ when uri.IsLoopback => (uri.Host, 0),
            // Kestrel's [::] socket is dual-mode, and ::1 is unreachable where IPv6 is disabled (a container)
            UriHostNameType.IPv4 when uri.Host == "0.0.0.0" => ("127.0.0.1", 1),
            UriHostNameType.IPv6 when uri.Host == "[::]" => ("127.0.0.1", 1),
            // Kestrel binds any other host name (than localhost) to every address
            UriHostNameType.Dns => ("127.0.0.1", 1),
            _ => (uri.Host, 2),
        };
        var schemeRank = uri.Scheme == "http" ? 0 : 3;
        return (new UriBuilder(uri.Scheme, host, uri.Port).Uri, rank + schemeRank);
    }
}
