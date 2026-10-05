using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// A <c>speak</c> call as claude's stream shows it: its text, and its <c>recap</c> (issue #466), each as voice would say
/// it, the recap null when it gave none, and its <c>outcome</c> (issue #467) as it was given, null when it gave none.
/// </summary>
public sealed record SpeakCall(string Text, string? Recap = null, string? Outcome = null);

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

    /// <summary>The <c>speak</c> calls an assistant line of the main conversation makes, by tool use id, with their text and recap as voice would say them.</summary>
    public static IReadOnlyList<(string ToolUseId, SpeakCall Call)> Calls(string rawJson) => Content(rawJson)?.Calls ?? [];

    /// <summary>The tool use ids a user line of the main conversation answers, each with whether its result was an error.</summary>
    public static IReadOnlyList<(string ToolUseId, bool IsError)> Results(string rawJson) => Content(rawJson)?.Results ?? [];

    private static (List<(string ToolUseId, SpeakCall Call)> Calls, List<(string ToolUseId, bool IsError)> Results)? Content(string rawJson)
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

            List<(string, SpeakCall)> calls = [];
            List<(string, bool)> results = [];
            foreach (var block in content.EnumerateArray())
            {
                switch (String(block, "type"))
                {
                    case "tool_use" when String(block, "name") == ToolName && String(block, "id") is { } id:
                        var input = block.TryGetProperty("input", out var given) ? given : default;
                        calls.Add((id, new SpeakCall(SpeakTool.Normalize(String(input, "text")),
                            SpeakTool.Normalize(String(input, "recap")) is { Length: > 0 } recap ? recap : null, String(input, "outcome"))));
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
