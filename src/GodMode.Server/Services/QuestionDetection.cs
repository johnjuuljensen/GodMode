using System.Text.Json;

namespace GodMode.Server.Services;

/// <summary>
/// Deterministic question detection for Claude's stream-json output.
///
/// Rule: the last text content block of the assistant message preceding a
/// <c>type=result</c> event is a question iff its trimmed text ends with
/// '?'. See issue #131 for the investigation behind this heuristic.
/// </summary>
public static class QuestionDetection
{
    /// <summary>
    /// Extracts the last <c>type="text"</c> content block from an assistant
    /// event's raw JSON. Returns null if the JSON is not an assistant event
    /// with a content array, or if no text block is present.
    /// </summary>
    public static string? ExtractLastAssistantText(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var message)) return null;
            if (!message.TryGetProperty("content", out var content)) return null;
            if (content.ValueKind != JsonValueKind.Array) return null;

            string? lastText = null;
            foreach (var item in content.EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var type)) continue;
                if (type.GetString() != "text") continue;
                if (!item.TryGetProperty("text", out var text)) continue;
                lastText = text.GetString();
            }
            return lastText;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true if the given assistant text block, after trimming trailing
    /// whitespace, ends with a question mark ('?').
    /// Empty/null input returns false.
    /// </summary>
    public static bool IsQuestion(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var trimmed = text.AsSpan().TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == '?';
    }
}
