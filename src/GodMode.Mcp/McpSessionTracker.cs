using System.Collections.Concurrent;
using ModelContextProtocol.Server;

namespace GodMode.Mcp;

/// <summary>
/// Tracks active MCP sessions so we can send notifications to them from HTTP endpoints.
/// This is necessary because MCP resources notifications need access to the McpServer instance.
/// </summary>
public class McpSessionTracker
{
    private readonly ConcurrentDictionary<string, McpServer> _sessions = new();

    public void Register(string sessionId, McpServer server)
    {
        _sessions[sessionId] = server;
    }

    public void Unregister(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public IEnumerable<McpServer> GetAllSessions() => _sessions.Values;

    public McpServer? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var server) ? server : null;
    }

    /// <summary>
    /// Sends a resource updated notification to all active sessions.
    /// </summary>
    public async Task NotifyResourceUpdatedAsync(string uri, CancellationToken cancellationToken = default)
    {
        foreach (var server in _sessions.Values)
        {
            try
            {
                await server.SendNotificationAsync(
                    "notifications/resources/updated",
                    new { uri },
                    cancellationToken: cancellationToken);
            }
            catch
            {
                // Session may have disconnected, ignore
            }
        }
    }

    /// <summary>
    /// Notifies all sessions that the list of resources has changed.
    /// Call this when a new agent is added or removed.
    /// </summary>
    public async Task NotifyResourceListChangedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var server in _sessions.Values)
        {
            try
            {
                await server.SendNotificationAsync(
                    "notifications/resources/list_changed",
                    new { },
                    cancellationToken: cancellationToken);
            }
            catch
            {
                // Session may have disconnected, ignore
            }
        }
    }
}
