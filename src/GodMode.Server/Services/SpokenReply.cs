using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// The turn's spoken reply as claude's stream shows it (issue #384): a <c>speak</c> call is the model's tool use in an
/// assistant line, and the server's answer to it the tool result in the user line after. Read from the stream, not
/// from the MCP call, so the text belongs to the turn whose lines carry it, between its start and its result, and to
/// the session's main conversation: a subagent's line names the tool use it runs under (<c>parent_tool_use_id</c>), and
/// is left out. A call is the turn's only once its result says it was not refused or denied.
/// </summary>
public static class SpokenReply
{
    /// <summary>How claude names the tool in its stream: <c>mcp__godmode__speak</c>.</summary>
    public const string ToolName = $"mcp__{ProjectManager.McpServerName}__{SpeakTool.Name}";

    /// <summary>The <c>speak</c> calls an assistant line of the main conversation makes, by tool use id, with their text as voice would say it.</summary>
    public static IReadOnlyList<(string ToolUseId, string Text)> Calls(string rawJson) => Content(rawJson)?.Calls ?? [];

    /// <summary>The tool use ids a user line of the main conversation answers, each with whether its result was an error.</summary>
    public static IReadOnlyList<(string ToolUseId, bool IsError)> Results(string rawJson) => Content(rawJson)?.Results ?? [];

    private static (List<(string ToolUseId, string Text)> Calls, List<(string ToolUseId, bool IsError)> Results)? Content(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // A subagent's line: what it says is not the session's reply
            if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String) return null;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;

            List<(string, string)> calls = [];
            List<(string, bool)> results = [];
            foreach (var block in content.EnumerateArray())
            {
                switch (String(block, "type"))
                {
                    case "tool_use" when String(block, "name") == ToolName && String(block, "id") is { } id:
                        calls.Add((id, SpeakTool.Normalize(block.TryGetProperty("input", out var input) ? String(input, "text") : null)));
                        break;
                    case "tool_result" when String(block, "tool_use_id") is { } answered:
                        results.Add((answered, block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True));
                        break;
                }
            }
            return (calls, results);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
