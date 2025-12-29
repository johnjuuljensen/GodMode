using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Represents a raw Claude output message with structured content parsing.
/// All properties are computed eagerly in the constructor for UI performance.
/// </summary>
public sealed class ClaudeMessage : INotifyPropertyChanged
{
    private const int MaxSummaryLength = 200;
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

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
    public string TypeDisplay { get; }

    /// <summary>
    /// Whether this is a user message (for alignment purposes).
    /// </summary>
    public bool IsUserMessage { get; }

    /// <summary>
    /// The simplified summary for this message.
    /// </summary>
    public string Summary { get; }

    /// <summary>
    /// Nested content items extracted from message.content array.
    /// Empty for types without nested content (system, result).
    /// </summary>
    public IReadOnlyList<ClaudeContentItem> ContentItems { get; }

    /// <summary>
    /// Whether this message has nested content items.
    /// </summary>
    public bool HasContentItems { get; }

    /// <summary>
    /// Concatenated summary of all content items for display.
    /// </summary>
    public string ContentSummary { get; }

    /// <summary>
    /// Pretty-printed JSON for expanded view.
    /// </summary>
    public string FormattedJson { get; }

    /// <summary>
    /// Creates a new ClaudeMessage from raw JSON.
    /// All parsing is done eagerly for UI performance.
    /// </summary>
    public ClaudeMessage(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;

            Type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? "unknown"
                : "unknown";
            Subtype = root.TryGetProperty("subtype", out var subtypeElement)
                ? subtypeElement.GetString()
                : null;
            TypeDisplay = Subtype != null ? $"{Type}:{Subtype}" : Type;
            IsUserMessage = Type == "user";
            Summary = ExtractSummary(root);
            ContentItems = ExtractContentItems(root);
            HasContentItems = ContentItems.Count > 0;
            ContentSummary = BuildContentSummary();
            FormattedJson = JsonSerializer.Serialize(document, IndentedOptions);
        }
        catch
        {
            Type = "error";
            Subtype = null;
            TypeDisplay = "error";
            IsUserMessage = false;
            Summary = rawJson.Length > MaxSummaryLength ? rawJson[..MaxSummaryLength] + "..." : rawJson;
            ContentItems = [];
            HasContentItems = false;
            ContentSummary = "";
            FormattedJson = rawJson;
        }
    }

    private string ExtractSummary(JsonElement root)
    {
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

    private static string ExtractResultSummary(JsonElement root)
    {
        if (root.TryGetProperty("result", out var result))
        {
            var text = result.GetString() ?? "";
            return text.Length > MaxSummaryLength ? text[..MaxSummaryLength] + "..." : text;
        }
        return "";
    }

    private string BuildContentSummary()
    {
        if (ContentItems.Count == 0) return "";

        // Build a simple text summary of all content items
        var parts = ContentItems.Select(item => $"[{item.Type}] {item.Summary}");
        var combined = string.Join("\n", parts);

        // Truncate if too long
        const int maxLength = 500;
        return combined.Length > maxLength ? combined[..maxLength] + "..." : combined;
    }

    private List<ClaudeContentItem> ExtractContentItems(JsonElement root)
    {
        var items = new List<ClaudeContentItem>();

        if (Type is not ("user" or "assistant")) return items;
        if (!root.TryGetProperty("message", out var message)) return items;
        if (!message.TryGetProperty("content", out var content)) return items;
        if (content.ValueKind != JsonValueKind.Array) return items;

        foreach (var item in content.EnumerateArray())
        {
            var contentItem = ClaudeContentItem.FromJsonElement(item);
            if (contentItem != null)
                items.Add(contentItem);
        }

        return items;
    }
}

/// <summary>
/// Represents a single content item within a message.content array.
/// All properties are computed eagerly for UI performance.
/// </summary>
public sealed class ClaudeContentItem : INotifyPropertyChanged
{
    private const int MaxSummaryLength = 300;
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

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
    /// Pretty-printed JSON for expanded view.
    /// </summary>
    public string FormattedJson { get; }

    private ClaudeContentItem(string type, string summary, string formattedJson)
    {
        Type = type;
        Summary = summary;
        FormattedJson = formattedJson;
    }

    /// <summary>
    /// Creates a ClaudeContentItem from a JSON element.
    /// </summary>
    public static ClaudeContentItem? FromJsonElement(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeElement))
            return null;

        var type = typeElement.GetString() ?? "unknown";
        var summary = type switch
        {
            "text" => ExtractTextSummary(element),
            "tool_use" => ExtractToolUseSummary(element),
            "tool_result" => ExtractToolResultSummary(element),
            _ => type
        };

        // Format JSON eagerly
        string formattedJson;
        try
        {
            formattedJson = JsonSerializer.Serialize(element, IndentedOptions);
        }
        catch
        {
            formattedJson = element.GetRawText();
        }

        return new ClaudeContentItem(type, summary, formattedJson);
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
}
