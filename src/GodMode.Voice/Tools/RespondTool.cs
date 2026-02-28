namespace GodMode.Voice.Tools;

/// <summary>
/// Fallback tool for plain text responses. The model calls this
/// instead of outputting raw text, keeping all output as tool calls.
/// </summary>
public sealed class RespondTool : ITool
{
    public string Name => "respond";
    public string Description => "Use this tool to reply to the user with a spoken message. Use for greetings, questions, help, or any response that isn't an action.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter
        {
            Name = "message",
            Type = "string",
            Description = "The message to speak to the user.",
            Required = true
        }
    };

    public Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var message = ExtractString(args, "message") ?? "I'm not sure what to say.";
        return Task.FromResult(ToolResult.Ok(Name, message));
    }

    private static string? ExtractString(IDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is System.Text.Json.JsonElement je) return je.GetString();
        return val?.ToString();
    }
}
