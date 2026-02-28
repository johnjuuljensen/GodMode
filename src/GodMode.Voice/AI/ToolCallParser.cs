using System.Text.Json;
using System.Text.RegularExpressions;
using GodMode.Voice.Tools;

namespace GodMode.Voice.AI;

public static class ToolCallParser
{
    /// <summary>
    /// Attempts to parse the LLM output into tool calls.
    /// Returns an empty list if the output is plain text (no tool call).
    /// </summary>
    public static List<ToolCall> Parse(string llmOutput)
    {
        var results = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(llmOutput))
            return results;

        var cleaned = CleanLlmOutput(llmOutput);

        // Try direct parse first
        var call = TryParseSingle(cleaned);
        if (call is not null)
        {
            results.Add(call);
            return results;
        }

        // Try extracting JSON object from surrounding text
        var extracted = ExtractJsonObject(cleaned);
        if (extracted is not null)
        {
            call = TryParseSingle(extracted);
            if (call is not null)
            {
                results.Add(call);
                return results;
            }
        }

        return results;
    }

    private static string CleanLlmOutput(string output)
    {
        var trimmed = output.Trim();

        // Strip markdown code fences (```json ... ``` or ``` ... ```)
        trimmed = Regex.Replace(trimmed, @"^```\w*\s*\n?", "");
        trimmed = Regex.Replace(trimmed, @"\n?```+\s*$", "");
        trimmed = trimmed.Trim();

        return trimmed;
    }

    /// <summary>
    /// Extracts the first balanced JSON object from a string,
    /// handling garbage before/after the JSON.
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        // Find the matching closing brace by counting depth
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}') depth--;

            if (depth == 0)
                return text[start..(i + 1)];
        }

        return null;
    }

    private static ToolCall? TryParseSingle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ToolCall? ParseElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty("tool", out var toolProp))
            return null;

        var toolName = toolProp.GetString();
        if (string.IsNullOrWhiteSpace(toolName))
            return null;

        var args = new Dictionary<string, object>();

        if (element.TryGetProperty("arguments", out var argsProp) &&
            argsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsProp.EnumerateObject())
            {
                args[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => (object)prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.Clone()
                };
            }
        }

        return new ToolCall { ToolName = toolName, Arguments = args };
    }
}
