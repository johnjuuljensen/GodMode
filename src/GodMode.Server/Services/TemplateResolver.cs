using System.Text.Json;
using System.Text.RegularExpressions;

namespace GodMode.Server.Services;

/// <summary>
/// Resolves {fieldName} placeholders in templates from input dictionaries.
/// </summary>
public static partial class TemplateResolver
{
    /// <summary>
    /// Resolves {fieldName} placeholders in a template string using values from the inputs dictionary.
    /// Unresolved placeholders are left as-is.
    /// </summary>
    public static string Resolve(string template, Dictionary<string, JsonElement> inputs)
    {
        return PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (inputs.TryGetValue(key, out var value))
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : value.ToString();
            return match.Value; // leave unresolved
        });
    }

    /// <summary>
    /// Gets a string value from inputs, or null if not present.
    /// </summary>
    public static string? GetString(Dictionary<string, JsonElement> inputs, string key)
    {
        if (inputs.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderRegex();
}
