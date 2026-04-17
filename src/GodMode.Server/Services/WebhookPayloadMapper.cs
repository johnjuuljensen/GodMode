using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Maps an incoming webhook JSON payload to project creation inputs
/// using the webhook's inputMapping and staticInputs configuration.
/// </summary>
public static class WebhookPayloadMapper
{
    /// <summary>
    /// Maps a webhook payload to project inputs.
    /// </summary>
    /// <param name="config">Webhook configuration with mapping rules.</param>
    /// <param name="payload">Raw JSON payload from the webhook request.</param>
    /// <returns>Inputs dictionary suitable for CreateProjectRequest.</returns>
    public static Dictionary<string, JsonElement> MapPayload(WebhookConfig config, JsonElement? payload)
    {
        var inputs = new Dictionary<string, JsonElement>();

        // Start with static inputs
        if (config.StaticInputs != null)
        {
            foreach (var (key, value) in config.StaticInputs)
                inputs[key] = value;
        }

        if (payload == null)
            return inputs;

        if (config.InputMapping is { Count: > 0 })
        {
            // Apply explicit field mapping from payload
            foreach (var (inputKey, jsonPath) in config.InputMapping)
            {
                var value = ExtractValue(payload.Value, jsonPath);
                if (value != null)
                    inputs[inputKey] = value.Value;
            }
        }
        else
        {
            // No mapping defined — use smart defaults for agent-to-agent use
            if (payload.Value.ValueKind == JsonValueKind.Object)
            {
                // If payload has a "prompt" field, use it directly
                if (payload.Value.TryGetProperty("prompt", out var promptProp))
                {
                    inputs["prompt"] = ToStringElement(promptProp);
                }
                else
                {
                    // Serialize entire payload as the prompt
                    inputs["prompt"] = JsonSerializer.SerializeToElement(payload.Value.ToString());
                }

                // If payload has a "name" field, use it
                if (payload.Value.TryGetProperty("name", out var nameProp))
                    inputs["name"] = ToStringElement(nameProp);
            }
            else if (payload.Value.ValueKind == JsonValueKind.String)
            {
                // Plain string body = prompt
                inputs["prompt"] = payload.Value;
            }
            else
            {
                // Anything else — serialize as prompt
                inputs["prompt"] = JsonSerializer.SerializeToElement(payload.Value.ToString());
            }
        }

        // Always pass the raw payload as a special input for scripts/templates
        if (payload != null)
            inputs["_webhookPayload"] = JsonSerializer.SerializeToElement(payload.Value.ToString());

        return inputs;
    }

    /// <summary>
    /// Extracts a value from a JSON element using a dot-path expression.
    /// Supports: $.field, $.nested.field, $.array[0].field
    /// </summary>
    private static JsonElement? ExtractValue(JsonElement root, string path)
    {
        // Strip leading "$." if present
        var cleanPath = path.StartsWith("$.") ? path[2..] : path;
        if (string.IsNullOrEmpty(cleanPath)) return root;

        var current = root;
        var segments = ParsePathSegments(cleanPath);

        foreach (var segment in segments)
        {
            if (segment.ArrayIndex.HasValue)
            {
                // Navigate to property first, then index
                if (!string.IsNullOrEmpty(segment.PropertyName))
                {
                    if (current.ValueKind != JsonValueKind.Object ||
                        !current.TryGetProperty(segment.PropertyName, out current))
                        return null;
                }

                if (current.ValueKind != JsonValueKind.Array ||
                    segment.ArrayIndex.Value >= current.GetArrayLength())
                    return null;

                current = current[segment.ArrayIndex.Value];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(segment.PropertyName, out current))
                    return null;
            }
        }

        return current;
    }

    private static List<PathSegment> ParsePathSegments(string path)
    {
        var segments = new List<PathSegment>();

        foreach (var part in path.Split('.'))
        {
            var bracketIdx = part.IndexOf('[');
            if (bracketIdx >= 0)
            {
                var propName = bracketIdx > 0 ? part[..bracketIdx] : "";
                var endBracket = part.IndexOf(']', bracketIdx);
                if (endBracket > bracketIdx + 1 &&
                    int.TryParse(part[(bracketIdx + 1)..endBracket], out var index))
                {
                    segments.Add(new PathSegment(propName, index));
                }
            }
            else
            {
                segments.Add(new PathSegment(part, null));
            }
        }

        return segments;
    }

    /// <summary>
    /// Ensures a JsonElement is converted to a string element for prompt/name inputs.
    /// </summary>
    private static JsonElement ToStringElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element;

        // Convert non-string values to their JSON string representation
        return JsonSerializer.SerializeToElement(element.ToString());
    }

    private readonly record struct PathSegment(string PropertyName, int? ArrayIndex);
}
