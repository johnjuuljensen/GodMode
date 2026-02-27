namespace GodMode.Voice.Tools;

public sealed class ToolCall
{
    public required string ToolName { get; init; }
    public IDictionary<string, object> Arguments { get; init; } = new Dictionary<string, object>();
}
