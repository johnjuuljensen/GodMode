using System.Collections.ObjectModel;

namespace GodMode.AI.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ITool> Tools => new ReadOnlyDictionary<string, ITool>(_tools);

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public ITool? GetTool(string name)
    {
        _tools.TryGetValue(name, out var tool);
        return tool;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call)
    {
        var tool = GetTool(call.ToolName);
        if (tool is null)
            return ToolResult.Fail(call.ToolName, $"Unknown tool: {call.ToolName}");

        try
        {
            return await tool.ExecuteAsync(call.Arguments);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(call.ToolName, $"Tool execution failed: {ex.Message}");
        }
    }

    public async Task<List<ToolResult>> ExecuteAsync(IEnumerable<ToolCall> calls)
    {
        var results = new List<ToolResult>();
        foreach (var call in calls)
        {
            results.Add(await ExecuteAsync(call));
        }
        return results;
    }
}
