using ModelContextProtocol.Client;

namespace GodMode.Server.Tests;

/// <summary>
/// Ceilings for what a test waits on, sized for a full parallel run of every test project on a busy
/// machine (#371), where starting pwsh or a server can take most of a minute. They are ceilings, not
/// delays: each wait returns as soon as its condition holds, so a generous one costs only a failing
/// test's time. A test whose subject is a timeout sets that timeout itself, and keeps it short.
/// </summary>
internal static class TestTimeouts
{
    /// <summary>A condition the test polls for: a state, a push, a line in a file.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>A server process to listen and answer <c>/health</c>.</summary>
    public static readonly TimeSpan ServerStart = TimeSpan.FromSeconds(180);

    /// <summary>One HTTP request, a hub handshake, or an MCP client's connect.</summary>
    public static readonly TimeSpan Request = TimeSpan.FromSeconds(120);

    /// <summary>A root script that is not the test's subject: the harness's list, issueInfo and status scripts.</summary>
    public static readonly TimeSpan Script = TimeSpan.FromSeconds(120);

    /// <summary>
    /// An MCP client's options for a test: the SDK's discover probe gives up after 5 s and falls back to
    /// <c>initialize</c>, which then goes out with the probe's protocol-version header and is refused (SDK 2.2),
    /// so the probe gets the whole connect.
    /// </summary>
    public static McpClientOptions McpClient() => new()
    {
        InitializationTimeout = Request,
        DiscoverProbeTimeout = Timeout.InfiniteTimeSpan,
    };
}
