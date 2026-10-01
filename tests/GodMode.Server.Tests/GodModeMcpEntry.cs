using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Auth;

namespace GodMode.Server.Tests;

/// <summary>
/// GodMode's entry in a launch's MCP config, its only one unless the session has the fleet's tools: the endpoint
/// claude calls and the headers it calls it with, which carry the project and the token that launch was issued.
/// </summary>
internal sealed record GodModeMcpEntry(string Type, string Url, IReadOnlyDictionary<string, string> Headers)
{
    public string ProjectId => Headers[ProjectTokenAuthenticationHandler.ProjectIdHeader];

    public string Token => Headers["Authorization"] is var authorization && authorization.StartsWith("Bearer ")
        ? authorization["Bearer ".Length..]
        : throw new InvalidOperationException($"not a bearer token: {authorization}");

    /// <summary>The config as the fake read it at start.</summary>
    public static GodModeMcpEntry Of(FakeLaunch launch) =>
        Parse(launch.McpConfig ?? throw new InvalidOperationException("the launch had no --mcp-config"));

    /// <summary>Asserts the config holds GodMode's server and nothing else, and returns its entry.</summary>
    public static GodModeMcpEntry Parse(string mcpConfigJson)
    {
        using var config = JsonDocument.Parse(mcpConfigJson);
        var server = Assert.Single(config.RootElement.GetProperty("mcpServers").EnumerateObject());
        Assert.Equal("godmode", server.Name);
        return Entry(server.Value);
    }

    /// <summary>
    /// Asserts the config of a session with the fleet's tools holds GodMode's server and the fleet's, and nothing
    /// else, and returns the fleet's entry, which calls with the same headers as GodMode's.
    /// </summary>
    public static GodModeMcpEntry FleetOf(FakeLaunch launch)
    {
        using var config = JsonDocument.Parse(launch.McpConfig ?? throw new InvalidOperationException("the launch had no --mcp-config"));
        var servers = config.RootElement.GetProperty("mcpServers").EnumerateObject().ToDictionary(server => server.Name, server => Entry(server.Value));
        Assert.Equal(["godmode", "godmode-fleet"], servers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(servers["godmode"].Headers.OrderBy(h => h.Key), servers["godmode-fleet"].Headers.OrderBy(h => h.Key));
        return servers["godmode-fleet"];
    }

    private static GodModeMcpEntry Entry(JsonElement server) => new(
        server.GetProperty("type").GetString()!,
        server.GetProperty("url").GetString()!,
        server.GetProperty("headers").EnumerateObject().ToDictionary(h => h.Name, h => h.Value.GetString()!));

    /// <summary>The config with the token taken out: what a create and its resumes share.</summary>
    public GodModeMcpEntry WithoutToken() =>
        this with { Headers = Headers.Where(h => h.Key != "Authorization").ToDictionary(h => h.Key, h => h.Value) };

    public bool Equals(GodModeMcpEntry? other) =>
        other != null && Type == other.Type && Url == other.Url
        && Headers.OrderBy(h => h.Key).SequenceEqual(other.Headers.OrderBy(h => h.Key));

    public override int GetHashCode() => HashCode.Combine(Type, Url, Headers.Count);
}
