using System.Text.Json;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// What a root's <c>issueInfo</c> script prints for the issue in <c>GODMODE_INPUT_ISSUE</c>, read strictly: the
/// output is untrusted, so anything but the documented object is refused rather than guessed at.
/// <code>
/// {"title": "Voice create keeps its draft", "labels": ["bug", "voice"]}
/// </code>
/// <c>title</c> is optional, <c>labels</c> an array of strings (empty for none). <c>{}</c> is an issue with neither.
/// </summary>
public static class IssueInfoScript
{
    /// <summary>The most stdout an issueInfo script may write; one that writes more is killed and refused.</summary>
    public const int MaxOutputChars = 64 * 1024;

    /// <summary>The most labels kept, and how long each, and the title, may be.</summary>
    public const int MaxLabels = 50;
    private const int MaxLabelLength = 100;
    private const int MaxTitleLength = 500;

    private static readonly JsonDocumentOptions Strict = new()
    {
        MaxDepth = 3,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>The issue the output reports. Throws <see cref="FormatException"/> saying what is wrong with anything else.</summary>
    public static IssueInfo Parse(string output)
    {
        if (output.Length > MaxOutputChars) throw new FormatException($"the output is longer than {MaxOutputChars} characters");

        JsonDocument document;
        try { document = JsonDocument.Parse(output, Strict); }
        catch (JsonException ex) { throw new FormatException($"the output is not one JSON object: {ex.Message}", ex); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException($"the output is a JSON {root.ValueKind}, not an object");
            if (root.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n is not ("title" or "labels")) is { } unknown)
                throw new FormatException($"unknown property '{Cut(unknown)}'");

            string? title = null;
            if (root.TryGetProperty("title", out var t) && t.ValueKind != JsonValueKind.Null)
                title = t.ValueKind == JsonValueKind.String
                    ? Cut(t.GetString()!, MaxTitleLength)
                    : throw new FormatException($"title is a JSON {t.ValueKind}, not a string");

            List<string> labels = [];
            if (root.TryGetProperty("labels", out var l) && l.ValueKind != JsonValueKind.Null)
            {
                if (l.ValueKind != JsonValueKind.Array) throw new FormatException($"labels is a JSON {l.ValueKind}, not an array");
                foreach (var label in l.EnumerateArray())
                {
                    if (label.ValueKind != JsonValueKind.String) throw new FormatException($"a label is a JSON {label.ValueKind}, not a string");
                    if (label.GetString()!.Trim() is { Length: > 0 } name && labels.Count < MaxLabels)
                        labels.Add(Cut(name, MaxLabelLength));
                }
            }
            return new IssueInfo(title, labels);
        }
    }

    private static string Cut(string text, int length = 80) => text.Length <= length ? text : text[..length] + "…";
}
