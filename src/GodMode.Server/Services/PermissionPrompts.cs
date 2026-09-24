using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Turns what claude sends the bridge's <c>permission_prompt</c> tool into a
/// <see cref="PendingRequest"/>, and the user's answer back into what claude expects.
/// </summary>
public static partial class PermissionPrompts
{
    /// <summary>claude's tool for asking the user questions; it reaches the permission prompt tool too.</summary>
    public const string AskUserQuestionTool = "AskUserQuestion";

    private const int MaxSummaryLength = 200;

    public static PendingRequest Create(PermissionPromptRequest request, string projectPath, DateTime now)
    {
        var id = Guid.NewGuid().ToString("N");
        if (request.ToolName == AskUserQuestionTool && ParseQuestions(request.Input) is { Count: > 0 } questions)
            return new PendingRequest(null, new PendingQuestion(id, questions, now), request.Input);

        var permission = new PendingPermission(id, request.ToolName, request.Input.Clone(),
            Summarize(request.ToolName, request.Input, projectPath), now);
        return new PendingRequest(permission, null, request.Input);
    }

    /// <summary>
    /// One line saying what the call does, for example <c>Bash: git push origin feature/12-x</c> or
    /// <c>Edit: src/Foo.cs</c> (a path inside the project is shown relative to it).
    /// </summary>
    public static string Summarize(string toolName, JsonElement input, string projectPath)
    {
        var detail = toolName switch
        {
            "Bash" or "PowerShell" => String(input, "command"),
            "Edit" or "MultiEdit" or "Write" or "Read" => RelativePath(String(input, "file_path"), projectPath),
            "NotebookEdit" => RelativePath(String(input, "notebook_path"), projectPath),
            "WebFetch" => String(input, "url"),
            "WebSearch" => String(input, "query"),
            "Glob" or "Grep" => String(input, "pattern"),
            "Task" or "Agent" => String(input, "description"),
            _ => FirstString(input),
        };
        return detail is { Length: > 0 } ? $"{toolName}: {OneLine(detail)}" : toolName;
    }

    /// <summary>What claude runs AskUserQuestion with: its input plus <c>answers</c>, question text to answer.</summary>
    public static JsonElement WithAnswers(JsonElement input, IReadOnlyDictionary<string, string> answers)
    {
        var node = JsonNode.Parse(input.GetRawText()) as JsonObject ?? new JsonObject();
        node["answers"] = JsonSerializer.SerializeToNode(answers);
        return JsonSerializer.SerializeToElement(node);
    }

    private static List<QuestionItem>? ParseQuestions(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array)
            return null;

        return questions.EnumerateArray()
            .Where(q => q.ValueKind == JsonValueKind.Object && String(q, "question") is { Length: > 0 })
            .Select(q => new QuestionItem(
                String(q, "question")!,
                String(q, "header"),
                q.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                    ? options.EnumerateArray()
                        .Where(o => o.ValueKind == JsonValueKind.Object && String(o, "label") is { Length: > 0 })
                        .Select(o => new QuestionOption(String(o, "label")!, String(o, "description")))
                        .ToList()
                    : [],
                q.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True))
            .ToList();
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FirstString(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
            ? input.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 })
                .Select(p => p.Value.GetString())
                .FirstOrDefault()
            : null;

    private static string? RelativePath(string? path, string projectPath)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return path;
        var relative = Path.GetRelativePath(projectPath, path);
        return relative.StartsWith("..") || Path.IsPathFullyQualified(relative) ? path : relative.Replace('\\', '/');
    }

    /// <summary>The first line, whitespace collapsed, cut to <see cref="MaxSummaryLength"/>; "…" marks what was left out.</summary>
    private static string OneLine(string text)
    {
        var lines = text.Trim().Split('\n');
        var line = Whitespace().Replace(lines[0], " ").Trim();
        var cut = lines.Length > 1 || line.Length > MaxSummaryLength;
        if (line.Length > MaxSummaryLength) line = line[..MaxSummaryLength].TrimEnd();
        return cut ? line + " …" : line;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
