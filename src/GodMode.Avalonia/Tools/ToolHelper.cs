using GodMode.Avalonia.Voice;
using GodMode.Voice.Services;

namespace GodMode.Avalonia.Tools;

internal static class ToolHelper
{
    public static string? ExtractString(IDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is System.Text.Json.JsonElement je) return je.GetString();
        return val?.ToString();
    }

    /// <summary>
    /// Formats a disambiguation list with numbered options, sorted by recency.
    /// </summary>
    public static string FormatDisambiguation(IReadOnlyList<IndexedProject> candidates)
    {
        var lines = new List<string> { "Multiple matches found. Say a number to select:" };

        for (var i = 0; i < candidates.Count; i++)
        {
            var p = candidates[i];
            var age = TimeFormatter.HumanizeDuration(DateTime.UtcNow - p.Summary.UpdatedAt);
            var state = p.Summary.State.ToString().ToLower();
            var question = p.Summary.CurrentQuestion is not null ? " [waiting for input]" : "";
            lines.Add($"{i + 1}. {p.Summary.Name} ({state}, {age} ago) on {p.HostName}{question}");
        }

        return string.Join("\n", lines);
    }
}
