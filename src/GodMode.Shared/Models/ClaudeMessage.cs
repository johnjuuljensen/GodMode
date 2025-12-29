using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Represents a raw Claude output message with structured content parsing.
/// </summary>
public sealed class ClaudeMessage : INotifyPropertyChanged
{
    private const int MaxSummaryLength = 200;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Whether this message is expanded to show full JSON.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _isExpanded;

    /// <summary>
    /// The raw JSON string from Claude's stream-json output.
    /// </summary>
    public string RawJson { get; }

    /// <summary>
    /// The parsed JSON document for rendering.
    /// </summary>
    public JsonDocument? Document { get; }

    /// <summary>
    /// The message type (system, user, assistant, result, error).
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// The subtype if present (e.g., "init" for system, "success" for result).
    /// </summary>
    public string? Subtype { get; }

    /// <summary>
    /// Combined type and subtype for display.
    /// </summary>
    public string TypeDisplay => Subtype != null ? $"{Type}:{Subtype}" : Type;

    /// <summary>
    /// Whether this is a user message (for alignment purposes).
    /// </summary>
    public bool IsUserMessage => Type == "user";

    /// <summary>
    /// The simplified summary for this message.
    /// </summary>
    public string Summary { get; }

    /// <summary>
    /// Nested content items extracted from message.content array.
    /// Empty for types without nested content (system, result).
    /// </summary>
    public IReadOnlyList<ClaudeContentItem> ContentItems => _contentItems ??= ExtractContentItems().ToList();
    private IReadOnlyList<ClaudeContentItem>? _contentItems;

    /// <summary>
    /// Whether this message has nested content items.
    /// </summary>
    public bool HasContentItems => ContentItems.Count > 0;

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
            Summary = ExtractSummary();
        }
        catch
        {
            Document = null;
            Type = "error";
            Subtype = null;
            Summary = rawJson.Length > MaxSummaryLength ? rawJson[..MaxSummaryLength] + "..." : rawJson;
        }
    }

    /// <summary>
    /// Extracts a simplified summary for the message based on its type.
    /// </summary>
    private string ExtractSummary()
    {
        if (Document == null) return "";

        var root = Document.RootElement;

        return Type switch
        {
            "system" => ExtractSystemSummary(root),
            "result" => ExtractResultSummary(root),
            "user" or "assistant" => "", // These use ContentItems instead
            _ => ""
        };
    }

    private string ExtractSystemSummary(JsonElement root)
    {
        var parts = new List<string>();

        if (Subtype != null)
            parts.Add(Subtype);

        if (root.TryGetProperty("session_id", out var sessionId))
        {
            var sid = sessionId.GetString() ?? "";
            if (sid.Length > 8)
                parts.Add($"session: {sid[..8]}...");
        }

        if (root.TryGetProperty("model", out var model))
            parts.Add(model.GetString() ?? "");

        return string.Join(" | ", parts);
    }

    private string ExtractResultSummary(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result))
        {
            var text = result.GetString() ?? "";
            return text.Length > MaxSummaryLength ? text[..MaxSummaryLength] + "..." : text;
        }
        return "";
    }

    /// <summary>
    /// Extracts content items from message.content array.
    /// </summary>
    private IEnumerable<ClaudeContentItem> ExtractContentItems()
    {
        if (Document == null) yield break;
        if (Type is not ("user" or "assistant")) yield break;

        var root = Document.RootElement;
        if (!root.TryGetProperty("message", out var message)) yield break;
        if (!message.TryGetProperty("content", out var content)) yield break;
        if (content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var item in content.EnumerateArray())
        {
            var contentItem = ClaudeContentItem.FromJsonElement(item);
            if (contentItem != null)
                yield return contentItem;
        }
    }

    /// <summary>
    /// Pretty-printed JSON for expanded view.
    /// </summary>
    public string FormattedJson => _formattedJson ??= FormatJson();
    private string? _formattedJson;

    private string FormatJson()
    {
        if (Document == null) return RawJson;

        try
        {
            return JsonSerializer.Serialize(Document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return RawJson;
        }
    }
}

/// <summary>
/// Represents a single content item within a message.content array.
/// </summary>
public sealed class ClaudeContentItem : INotifyPropertyChanged
{
    private const int MaxSummaryLength = 300;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Whether this content item is expanded to show full JSON.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }
    private bool _isExpanded;

    /// <summary>
    /// The content type (text, tool_use, tool_result).
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Simplified summary of the content.
    /// </summary>
    public string Summary { get; }

    /// <summary>
    /// The raw JSON element for expanded view.
    /// </summary>
    public string RawJson { get; }

    private ClaudeContentItem(string type, string summary, string rawJson)
    {
        Type = type;
        Summary = summary;
        RawJson = rawJson;
    }

    /// <summary>
    /// Creates a ClaudeContentItem from a JSON element.
    /// </summary>
    public static ClaudeContentItem? FromJsonElement(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeElement))
            return null;

        var type = typeElement.GetString() ?? "unknown";
        var rawJson = element.GetRawText();
        var summary = type switch
        {
            "text" => ExtractTextSummary(element),
            "tool_use" => ExtractToolUseSummary(element),
            "tool_result" => ExtractToolResultSummary(element),
            _ => type
        };

        return new ClaudeContentItem(type, summary, rawJson);
    }

    private static string ExtractTextSummary(JsonElement element)
    {
        if (element.TryGetProperty("text", out var text))
        {
            var str = text.GetString() ?? "";
            return str.Length > MaxSummaryLength ? str[..MaxSummaryLength] + "..." : str;
        }
        return "";
    }

    private static string ExtractToolUseSummary(JsonElement element)
    {
        var parts = new List<string>();

        if (element.TryGetProperty("name", out var name))
            parts.Add(name.GetString() ?? "");

        // Try to get a description from input if available
        if (element.TryGetProperty("input", out var input))
        {
            // For common tools, extract meaningful info
            if (input.TryGetProperty("file_path", out var filePath))
                parts.Add(filePath.GetString() ?? "");
            else if (input.TryGetProperty("pattern", out var pattern))
                parts.Add($"pattern: {pattern.GetString()}");
            else if (input.TryGetProperty("command", out var command))
            {
                var cmd = command.GetString() ?? "";
                if (cmd.Length > 50) cmd = cmd[..50] + "...";
                parts.Add(cmd);
            }
            else if (input.TryGetProperty("query", out var query))
            {
                var q = query.GetString() ?? "";
                if (q.Length > 50) q = q[..50] + "...";
                parts.Add(q);
            }
        }

        return string.Join(" → ", parts);
    }

    private static string ExtractToolResultSummary(JsonElement element)
    {
        if (element.TryGetProperty("content", out var content))
        {
            var str = content.GetString() ?? "";
            // Take first line or first N chars
            var firstLine = str.Split('\n')[0];
            return firstLine.Length > MaxSummaryLength ? firstLine[..MaxSummaryLength] + "..." : firstLine;
        }
        return "";
    }

    /// <summary>
    /// Pretty-printed JSON for expanded view.
    /// </summary>
    public string FormattedJson => _formattedJson ??= FormatJson();
    private string? _formattedJson;

    private string FormatJson()
    {
        try
        {
            using var doc = JsonDocument.Parse(RawJson);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return RawJson;
        }
    }
}
