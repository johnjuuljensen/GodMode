using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// What a root's <c>list</c> script prints, read strictly: the output is untrusted, so anything but the
/// documented array is refused, whole, saying why, rather than guessed at. One JSON array (empty output
/// is an empty one), each item a candidate folder:
/// <code>
/// [{"path": "feature-12", "name": "Fix the list", "kind": "feat", "action": "issue", "inputs": {"issue": 12}}]
/// </code>
/// <c>path</c> is required: a folder directly in the root, absolute or relative to it, that exists.
/// <c>name</c> (else the folder's name), <c>kind</c>, <c>action</c> (one of the root's actions that starts
/// a session; else the root's first) and <c>inputs</c> (an object of strings, numbers and booleans) are optional.
/// </summary>
public static class UnmanagedList
{
    /// <summary>The most stdout a list script may write; one that writes more is killed and refused.</summary>
    public const int MaxOutputChars = 1024 * 1024;

    /// <summary>The most candidates one listing gives.</summary>
    public const int MaxCandidates = 1000;

    private const int MaxTextLength = 200;

    private static readonly JsonDocumentOptions Strict = new()
    {
        MaxDepth = 4,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    private static readonly string[] Fields = ["path", "name", "kind", "action", "inputs"];

    /// <summary>
    /// The candidates the output lists, in its order, each with its path relative to the root at
    /// <paramref name="rootPath"/>. <paramref name="whyNotAdoptable"/> says why a path is no folder the
    /// root can adopt (null when it is). Throws <see cref="FormatException"/> saying what is wrong.
    /// </summary>
    public static IReadOnlyList<UnmanagedFolder> Parse(string output, string rootPath, RootConfig config, Func<string, string, string?> whyNotAdoptable)
    {
        if (output.Length > MaxOutputChars) throw new FormatException($"the output is longer than {MaxOutputChars} characters");
        if (string.IsNullOrWhiteSpace(output)) return [];

        JsonDocument document;
        try { document = JsonDocument.Parse(output, Strict); }
        catch (JsonException ex) { throw new FormatException($"the output is not one JSON array: {ex.Message}", ex); }

        using (document)
        {
            var list = document.RootElement;
            if (list.ValueKind != JsonValueKind.Array) throw new FormatException($"the output is a JSON {list.ValueKind}, not an array");
            if (list.GetArrayLength() > MaxCandidates) throw new FormatException($"it lists more than {MaxCandidates} folders");

            var candidates = new List<UnmanagedFolder>();
            var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                var at = $"item {index++}";
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException($"{at} is a JSON {item.ValueKind}, not an object");
                if (item.EnumerateObject().Select(p => p.Name).FirstOrDefault(name => !Fields.Contains(name)) is { } unknown)
                    throw new FormatException($"{at} has an unknown property '{Cut(unknown)}'");

                var path = Text(item, "path", at) ?? throw new FormatException($"{at} has no path");
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(rootPath, path)));
                if (whyNotAdoptable(rootPath, full) is { } reason) throw new FormatException($"{at}'s path '{Cut(path)}' {reason}");
                var folder = Path.GetFileName(full);
                if (!seen.Add(folder)) throw new FormatException($"{at}'s path '{Cut(path)}' is listed twice");

                var action = Text(item, "action", at);
                if (action != null && config.ResolveAction(action) is not { Session: true })
                    throw new FormatException($"{at}'s action '{Cut(action)}' is no action of the root that starts a session");

                candidates.Add(new UnmanagedFolder(folder, Text(item, "name", at) ?? folder, Text(item, "kind", at),
                    action == null ? null : config.ResolveAction(action)!.Name, Inputs(item, at)));
            }
            return candidates;
        }
    }

    /// <summary>A string property, not empty and of at most <see cref="MaxTextLength"/> characters; null when missing or null.</summary>
    private static string? Text(JsonElement item, string name, string at)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException($"{at}'s {name} is a JSON {value.ValueKind}, not a string");
        var text = value.GetString()!.Trim();
        return text.Length == 0 ? throw new FormatException($"{at}'s {name} is empty")
            : text.Length > MaxTextLength ? throw new FormatException($"{at}'s {name} is longer than {MaxTextLength} characters")
            : text;
    }

    private static Dictionary<string, JsonElement>? Inputs(JsonElement item, string at)
    {
        if (!item.TryGetProperty("inputs", out var inputs) || inputs.ValueKind == JsonValueKind.Null) return null;
        if (inputs.ValueKind != JsonValueKind.Object) throw new FormatException($"{at}'s inputs is a JSON {inputs.ValueKind}, not an object");
        var result = new Dictionary<string, JsonElement>();
        foreach (var input in inputs.EnumerateObject())
        {
            if (input.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                throw new FormatException($"{at}'s input '{Cut(input.Name)}' is a JSON {input.Value.ValueKind}, not a string, number or boolean");
            result[input.Name] = input.Value.Clone();
        }
        return result;
    }

    /// <summary>A piece of the output, short enough to say.</summary>
    private static string Cut(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
