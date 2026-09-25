using System.Text.Json;
using GodMode.FakeClaude;
using GodMode.Server.Auth;

namespace GodMode.Server.Tests;

/// <summary>
/// GodMode's entry in a launch's MCP config, its only one: the endpoint claude calls and the
/// headers it calls it with, which carry the project and the token that launch was issued.
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
        return new GodModeMcpEntry(
            server.Value.GetProperty("type").GetString()!,
            server.Value.GetProperty("url").GetString()!,
            server.Value.GetProperty("headers").EnumerateObject().ToDictionary(h => h.Name, h => h.Value.GetString()!));
    }

    /// <summary>The config with the token taken out: what a create and its resumes share.</summary>
    public GodModeMcpEntry WithoutToken() =>
        this with { Headers = Headers.Where(h => h.Key != "Authorization").ToDictionary(h => h.Key, h => h.Value) };

    public bool Equals(GodModeMcpEntry? other) =>
        other != null && Type == other.Type && Url == other.Url
        && Headers.OrderBy(h => h.Key).SequenceEqual(other.Headers.OrderBy(h => h.Key));

    public override int GetHashCode() => HashCode.Combine(Type, Url, Headers.Count);
}
