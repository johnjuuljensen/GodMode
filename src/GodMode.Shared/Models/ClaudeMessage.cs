using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Represents a raw Claude output message with light parsing for UI display.
/// The raw JSON is preserved for full rendering in the UI.
/// </summary>
public sealed class ClaudeMessage
{
    private const int MaxStringLength = 300;

    /// <summary>
    /// The raw JSON string from Claude's stream-json output.
    /// </summary>
    public string RawJson { get; }

    /// <summary>
    /// The parsed JSON document for rendering.
    /// </summary>
    public JsonDocument? Document { get; }

    /// <summary>
    /// The message type extracted from the "type" field (system, user, assistant, result, error).
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// The subtype extracted from the "subtype" field (e.g., tool_use, tool_result).
    /// </summary>
    public string? Subtype { get; }

    /// <summary>
    /// Combined type and subtype for display (e.g., "assistant:tool_use").
    /// </summary>
    public string TypeDisplay => Subtype != null ? $"{Type}:{Subtype}" : Type;

    /// <summary>
    /// Whether this is a user message (for alignment purposes).
    /// </summary>
    public bool IsUserMessage => Type == "user";

    /// <summary>
    /// Simple content for display in simple mode.
    /// Extracts result content or message.content of type text.
    /// </summary>
    public string? SimpleContent => _simpleContent ??= ExtractSimpleContent();
    private string? _simpleContent;

    /// <summary>
    /// Whether this message should be shown in simple mode.
    /// Only result types and messages with text content are shown.
    /// </summary>
    public bool ShowInSimpleMode => !string.IsNullOrEmpty(SimpleContent);

    /// <summary>
    /// Gets all properties for UI binding. Caches the result.
    /// </summary>
    public IReadOnlyList<ClaudeMessageProperty> Properties => _properties ??= GetProperties().ToList();
    private IReadOnlyList<ClaudeMessageProperty>? _properties;

    /// <summary>
    /// Creates a new ClaudeMessage from raw JSON.
    /// </summary>
    public ClaudeMessage(string rawJson)
    {
        RawJson = rawJson;

        try
        {
            Document = JsonDocument.Parse(rawJson);
            Type = Document.RootElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? "unknown"
                : "unknown";
            Subtype = Document.RootElement.TryGetProperty("subtype", out var subtypeElement)
                ? subtypeElement.GetString()
                : null;
        }
        catch
        {
            Document = null;
            Type = "error";
            Subtype = null;
        }
    }

    /// <summary>
    /// Extracts simple content from result or message.content text.
    /// </summary>
    private string? ExtractSimpleContent()
    {
        if (Document == null) return null;

        var root = Document.RootElement;

        // For "result" type, extract the result text
        if (Type == "result" && root.TryGetProperty("result", out var resultElement))
        {
            return resultElement.GetString();
        }

        // For user/assistant types, look for message.content with type=text
        if ((Type == "user" || Type == "assistant") && root.TryGetProperty("message", out var messageElement))
        {
            if (messageElement.TryGetProperty("content", out var contentElement) &&
                contentElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentElement.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var itemType) &&
                        itemType.GetString() == "text" &&
                        item.TryGetProperty("text", out var textElement))
                    {
                        return textElement.GetString();
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the properties of the root object for rendering.
    /// </summary>
    public IEnumerable<ClaudeMessageProperty> GetProperties()
    {
        if (Document == null)
        {
            yield return new ClaudeMessageProperty("raw", RawJson, 0);
            yield break;
        }

        foreach (var property in Document.RootElement.EnumerateObject())
        {
            foreach (var rendered in RenderProperty(property.Name, property.Value, 0))
            {
                yield return rendered;
            }
        }
    }

    private static IEnumerable<ClaudeMessageProperty> RenderProperty(string name, JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var str = element.GetString() ?? "";
                var truncated = str.Length > MaxStringLength ? str[..MaxStringLength] + "..." : str;
                yield return new ClaudeMessageProperty(name, truncated, depth);
                break;

            case JsonValueKind.Number:
                yield return new ClaudeMessageProperty(name, element.GetRawText(), depth);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                yield return new ClaudeMessageProperty(name, element.GetBoolean().ToString().ToLower(), depth);
                break;

            case JsonValueKind.Null:
                yield return new ClaudeMessageProperty(name, "null", depth);
                break;

            case JsonValueKind.Array:
                yield return new ClaudeMessageProperty(name, $"[{element.GetArrayLength()} items]", depth, true);
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var rendered in RenderProperty($"[{index}]", item, depth + 1))
                    {
                        yield return rendered;
                    }
                    index++;
                }
                break;

            case JsonValueKind.Object:
                yield return new ClaudeMessageProperty(name, "{...}", depth, true);
                foreach (var prop in element.EnumerateObject())
                {
                    foreach (var rendered in RenderProperty(prop.Name, prop.Value, depth + 1))
                    {
                        yield return rendered;
                    }
                }
                break;
        }
    }
}

/// <summary>
/// A single property from a ClaudeMessage for display.
/// </summary>
public record ClaudeMessageProperty(
    string Name,
    string Value,
    int Depth,
    bool IsContainer = false
)
{
    /// <summary>
    /// Indentation string based on depth.
    /// </summary>
    public string Indent => new(' ', Depth * 2);

    /// <summary>
    /// Formatted display string.
    /// </summary>
    public string Display => IsContainer
        ? $"{Indent}{Name}: {Value}"
        : $"{Indent}{Name}: {Value}";
}
