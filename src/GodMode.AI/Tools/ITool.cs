namespace GodMode.AI.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolParameter> Parameters { get; }
    Task<ToolResult> ExecuteAsync(IDictionary<string, object> args);
}
