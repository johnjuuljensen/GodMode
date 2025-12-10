using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace GodMode.Mcp.Resources;

[McpServerResourceType]
public class AgentResources
{
    private readonly AgentOutputStore _store;

    public AgentResources(AgentOutputStore store)
    {
        _store = store;
    }

    [McpServerResource(UriTemplate = "agent://{name}/output")]
    [Description("Real-time output stream from a Claude Code agent running on a codespace. Subscribe to receive notifications when new output is available.")]
    public string GetAgentOutput(string name)
    {
        var output = _store.GetOutput(name);
        return JsonSerializer.Serialize(new
        {
            agent = name,
            lines = output,
            timestamp = DateTime.UtcNow
        });
    }
}

/// <summary>
/// In-memory store for agent output. In production, this would be backed by
/// actual output streaming from the codespace controller daemon.
/// </summary>
public class AgentOutputStore
{
    private readonly Dictionary<string, List<string>> _outputs = new();
    private readonly object _lock = new();

    /// <summary>
    /// Appends output to an agent. Returns true if this is a new agent (first output).
    /// </summary>
    public bool AppendOutput(string agentName, string line)
    {
        lock (_lock)
        {
            var isNew = !_outputs.ContainsKey(agentName);
            if (isNew)
            {
                _outputs[agentName] = [];
            }
            _outputs[agentName].Add(line);
            return isNew;
        }
    }

    public List<string> GetOutput(string agentName)
    {
        lock (_lock)
        {
            return _outputs.TryGetValue(agentName, out var lines)
                ? [.. lines]
                : [];
        }
    }

    public List<string> GetAllAgentNames()
    {
        lock (_lock)
        {
            return [.. _outputs.Keys];
        }
    }

    public void Clear(string agentName)
    {
        lock (_lock)
        {
            _outputs.Remove(agentName);
        }
    }
}
