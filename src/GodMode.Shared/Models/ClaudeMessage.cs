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
    /// Single-character initial for avatar display.
    /// </summary>
    public string TypeInitial { get; }

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
    /// Whether any content item in this message is an error tool result (is_error: true).
    /// </summary>
    public bool HasErrorContent { get; }

    /// <summary>
    /// Concatenated summary of all content items for display.
    /// </summary>
    public string ContentSummary { get; }

    /// <summary>
    /// Pretty-printed JSON for expanded view.
    /// </summary>
    public string FormattedJson { get; }

    /// <summary>
    /// Whether this message contains only tool_use/tool_result content items (no text).
    /// Used by simple chat view to identify messages that should be collapsed.
    /// </summary>
    public bool IsToolOnly { get; }

    /// <summary>
    /// Content summary containing only text items (no tool_use/tool_result).
    /// Used by simple chat view to show text-only content for mixed messages.
    /// </summary>
    public string TextOnlyContentSummary { get; }

    /// <summary>
    /// Whether this message is a question/permission prompt requiring user input.
    /// </summary>
    public bool IsQuestion { get; }

    /// <summary>
    /// The question text if this is a question message.
    /// </summary>
    public string? QuestionText { get; }

    /// <summary>
    /// Available options for question messages.
    /// </summary>
    public IReadOnlyList<QuestionOptionData> QuestionOptions { get; }

    /// <summary>
    /// Short header/category label from the question (e.g., "Auth method", "Library").
    /// </summary>
    public string? QuestionHeader { get; }

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
            TypeInitial = Type switch
            {
                "system" => "S",
                "user" => "U",
                "assistant" => "A",
                "result" => "R",
                "error" => "!",
                _ => "?"
            };
            Summary = ExtractSummary(root);
            ContentItems = ExtractContentItems(root);
            HasContentItems = ContentItems.Count > 0;
            HasErrorContent = ContentItems.Any(i => i.IsError);
            FormattedJson = JsonSerializer.Serialize(document, IndentedOptions);

            (IsQuestion, QuestionText, QuestionOptions, QuestionHeader) = ExtractQuestionData(root);

            // Fallback: if ExtractQuestionData didn't detect the question (e.g., due to JSON
            // path differences), check whether any content item is an AskUserQuestion tool_use
            if (!IsQuestion)
            {
                var askItem = ContentItems.FirstOrDefault(i => i.ToolName == "AskUserQuestion");
                if (askItem != null)
                {
                    IsQuestion = true;
                    QuestionText = askItem.ToolQuestionText ?? "Waiting for input";
                    QuestionOptions = askItem.ToolQuestionOptions ?? [];
                    QuestionHeader = askItem.ToolQuestionHeader;
                }
            }

            // Question messages should display the question text and not be collapsed as tool calls
            IsToolOnly = !IsQuestion && HasContentItems && ContentItems.All(i => i.Type is "tool_use" or "tool_result");
            ContentSummary = IsQuestion && !string.IsNullOrEmpty(QuestionText)
                ? QuestionText!
                : BuildContentSummary();
            TextOnlyContentSummary = IsQuestion && !string.IsNullOrEmpty(QuestionText)
                ? QuestionText!
                : BuildTextOnlyContentSummary();

        }
        catch
        {
            Type = "error";
            Subtype = null;
            TypeDisplay = "error";
            IsUserMessage = false;
            TypeInitial = "!";
            Summary = rawJson.Length > MaxSummaryLength ? rawJson[..MaxSummaryLength] + "..." : rawJson;
            ContentItems = [];
            HasContentItems = false;
            HasErrorContent = false;
            ContentSummary = "";
            FormattedJson = rawJson;
            IsToolOnly = false;
            TextOnlyContentSummary = "";
            IsQuestion = false;
            QuestionText = null;
            QuestionOptions = [];
            QuestionHeader = null;
        }
    }

    private static (bool isQuestion, string? text, IReadOnlyList<QuestionOptionData> options, string? header) ExtractQuestionData(JsonElement root)
    {
        // Claude Code question prompts come as assistant messages with specific patterns
        // Check for tool_use with AskUserQuestion or permission prompts
        if (!TryGetContentArray(root, out var content)) return (false, null, [], null);

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type)) continue;
            if (type.GetString() != "tool_use") continue;
            if (!item.TryGetProperty("name", out var name)) continue;

            var toolName = name.GetString();
            if (toolName == "AskUserQuestion" && item.TryGetProperty("input", out var input))
            {
                // AskUserQuestion uses "questions" (plural array) as the top-level input key.
                // Each element has question, header, options, multiSelect.
                // Fall back to singular "question"/"options" for backward compat.
                var source = input;
                if (input.TryGetProperty("questions", out var questionsArr) &&
                    questionsArr.ValueKind == JsonValueKind.Array && questionsArr.GetArrayLength() > 0)
                {
                    source = questionsArr[0];
                }

                var questionText = source.TryGetProperty("question", out var q)
                    ? q.GetString() : null;
                var header = source.TryGetProperty("header", out var h)
                    ? h.GetString() : null;
                var options = new List<QuestionOptionData>();

                if (source.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var opt in opts.EnumerateArray())
                    {
                        var label = opt.TryGetProperty("label", out var l)
                            ? l.GetString() ?? "" : "";
                        var description = opt.TryGetProperty("description", out var d)
                            ? d.GetString() : null;
                        options.Add(new QuestionOptionData(label, description));
                    }
                }

                return (true, questionText, options, header);
            }
        }

        return (false, null, [], null);
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

    private string BuildTextOnlyContentSummary()
    {
        if (ContentItems.Count == 0) return "";

        var textParts = ContentItems
            .Where(i => i.Type == "text")
            .Select(i => i.Summary);

        return string.Join("\n", textParts);
    }

    private string BuildContentSummary()
    {
        if (ContentItems.Count == 0) return "";

        // Build a summary of all content items
        // Text items show full content without prefix, tool items get prefixed for clarity
        var parts = ContentItems.Select(item => item.Type switch
        {
            "text" => item.Summary,
            "tool_result" when item.IsError => $"[ERROR] {item.Summary}",
            _ => $"[{item.Type}] {item.Summary}"
        });

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Finds the content array from multiple possible JSON paths:
    /// root.message.content, root.content
    /// </summary>
    private static bool TryGetContentArray(JsonElement root, out JsonElement content)
    {
        content = default;

        // Primary path: root.message.content (standard stream-json format)
        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        // Fallback: root.content (alternative format)
        if (root.TryGetProperty("content", out content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        return false;
    }

    private List<ClaudeContentItem> ExtractContentItems(JsonElement root)
    {
        var items = new List<ClaudeContentItem>();

        if (Type is not ("user" or "assistant")) return items;
        if (!TryGetContentArray(root, out var content)) return items;

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

    // Rich tool rendering properties
    /// <summary>Tool name for tool_use items (e.g., "Edit", "Write", "Bash").</summary>
    public string? ToolName { get; private set; }
    /// <summary>File path for Edit/Write/Read tools.</summary>
    public string? ToolFilePath { get; private set; }
    /// <summary>Old string for Edit tool diffs.</summary>
    public string? ToolOldString { get; private set; }
    /// <summary>New string for Edit tool diffs.</summary>
    public string? ToolNewString { get; private set; }
    /// <summary>Command string for Bash tool.</summary>
    public string? ToolCommand { get; private set; }
    /// <summary>Description for Bash tool.</summary>
    public string? ToolDescription { get; private set; }
    /// <summary>Content for Write tool.</summary>
    public string? ToolContent { get; private set; }
    /// <summary>Whether this tool_result has is_error set to true.</summary>
    public bool IsError { get; private set; }
    /// <summary>Question text extracted from AskUserQuestion tool_use input.</summary>
    public string? ToolQuestionText { get; private set; }
    /// <summary>Question options extracted from AskUserQuestion tool_use input.</summary>
    public IReadOnlyList<QuestionOptionData>? ToolQuestionOptions { get; private set; }
    /// <summary>Question header extracted from AskUserQuestion tool_use input.</summary>
    public string? ToolQuestionHeader { get; private set; }

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

        var item = new ClaudeContentItem(type, summary, formattedJson);

        // Extract tool-specific data for rich rendering
        if (type == "tool_result")
        {
            item.IsError = element.TryGetProperty("is_error", out var isError)
                && isError.ValueKind == JsonValueKind.True;
        }
        else if (type == "tool_use")
        {
            item.ToolName = element.TryGetProperty("name", out var toolName)
                ? toolName.GetString() : null;

            if (element.TryGetProperty("input", out var input))
            {
                item.ToolFilePath = input.TryGetProperty("file_path", out var fp)
                    ? fp.GetString() : null;
                item.ToolOldString = input.TryGetProperty("old_string", out var os)
                    ? os.GetString() : null;
                item.ToolNewString = input.TryGetProperty("new_string", out var ns)
                    ? ns.GetString() : null;
                item.ToolCommand = input.TryGetProperty("command", out var cmd)
                    ? cmd.GetString() : null;
                item.ToolDescription = input.TryGetProperty("description", out var desc)
                    ? desc.GetString() : null;
                item.ToolContent = input.TryGetProperty("content", out var cnt)
                    ? cnt.GetString() : null;

                // Extract question data from AskUserQuestion tool_use
                if (item.ToolName == "AskUserQuestion")
                    ExtractAskUserQuestionData(item, input);
            }
        }

        return item;
    }

    private static void ExtractAskUserQuestionData(ClaudeContentItem item, JsonElement input)
    {
        // Navigate to the first question, handling both plural "questions" array and singular "question" key
        var source = input;
        if (input.TryGetProperty("questions", out var questionsArr) &&
            questionsArr.ValueKind == JsonValueKind.Array && questionsArr.GetArrayLength() > 0)
        {
            source = questionsArr[0];
        }

        item.ToolQuestionText = source.TryGetProperty("question", out var q)
            ? q.GetString() : null;
        item.ToolQuestionHeader = source.TryGetProperty("header", out var h)
            ? h.GetString() : null;

        var options = new List<QuestionOptionData>();
        if (source.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var opt in opts.EnumerateArray())
            {
                var label = opt.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                var description = opt.TryGetProperty("description", out var d) ? d.GetString() : null;
                options.Add(new QuestionOptionData(label, description));
            }
        }
        item.ToolQuestionOptions = options;
    }

    private static string ExtractTextSummary(JsonElement element)
    {
        if (element.TryGetProperty("text", out var text))
        {
            return text.GetString() ?? "";
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

/// <summary>
/// Data for a single question option with label and optional description.
/// </summary>
public record QuestionOptionData(string Label, string? Description);
