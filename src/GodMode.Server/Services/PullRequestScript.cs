using System.Text.Json;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// What a root's <c>status</c> script prints, read strictly: the output is untrusted, so anything
/// but the documented object is refused rather than guessed at.
/// <code>
/// {"pullRequest": {"url": "https://…", "number": 12, "state": "draft|open|merged|closed", "review": "none|changes_requested|approved"},
///  "progress": {"resolved": 4, "total": 12}}
/// </code>
/// each optional: <c>{}</c> (or <c>{"pullRequest": null}</c>) when there is no pull request, and no
/// <c>progress</c> (or <c>null</c>) when the script reports none (issue #545).
/// </summary>
public static class PullRequestScript
{
    /// <summary>The most stdout a status script may write; one that writes more is killed and ignored.</summary>
    public const int MaxOutputChars = 16 * 1024;

    private const int MaxUrlLength = 2048;

    private static readonly JsonDocumentOptions Strict = new()
    {
        MaxDepth = 4,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    private static readonly Dictionary<string, PullRequestState> States = new(StringComparer.Ordinal)
    {
        ["draft"] = PullRequestState.Draft,
        ["open"] = PullRequestState.Open,
        ["merged"] = PullRequestState.Merged,
        ["closed"] = PullRequestState.Closed,
    };

    private static readonly Dictionary<string, PullRequestReview> Reviews = new(StringComparer.Ordinal)
    {
        ["none"] = PullRequestReview.None,
        ["changes_requested"] = PullRequestReview.ChangesRequested,
        ["approved"] = PullRequestReview.Approved,
    };

    private static readonly string[] Fields = ["url", "number", "state", "review"];

    private static readonly string[] ProgressFields = ["resolved", "total"];

    /// <summary>What one run of a status script reported.</summary>
    /// <param name="PullRequest">The project's pull request, or null for none.</param>
    /// <param name="Progress">How far its work has got, or null for none reported.</param>
    public record Report(PullRequestStatus? PullRequest, WorkProgress? Progress);

    /// <summary>
    /// The pull request the output reports, seen at <paramref name="now"/>, and the progress, each null for none.
    /// Throws <see cref="FormatException"/> saying what is wrong with anything else: a bad progress fails the whole
    /// report, as a bad pull request does.
    /// </summary>
    public static Report Parse(string output, DateTime now)
    {
        if (output.Length > MaxOutputChars) throw new FormatException($"the output is longer than {MaxOutputChars} characters");

        JsonDocument document;
        try { document = JsonDocument.Parse(output, Strict); }
        catch (JsonException ex) { throw new FormatException($"the output is not one JSON object: {ex.Message}", ex); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException($"the output is a JSON {root.ValueKind}, not an object");
            if (Unknown(root, ["pullRequest", "progress"]) is { } unknown)
                throw new FormatException($"unknown property '{Cut(unknown)}'");
            return new Report(PullRequest(root, now), Progress(root));
        }
    }

    private static PullRequestStatus? PullRequest(JsonElement root, DateTime now)
    {
        if (Member(root, "pullRequest", Fields) is not { } pr) return null;
        return new PullRequestStatus(
            Url(Required(pr, "pullRequest", "url", JsonValueKind.String).GetString()!),
            Required(pr, "pullRequest", "number", JsonValueKind.Number).TryGetInt32(out var number) && number > 0
                ? number
                : throw new FormatException("pullRequest.number is not a positive whole number"),
            OneOf(States, Required(pr, "pullRequest", "state", JsonValueKind.String).GetString()!, "state"),
            OneOf(Reviews, Required(pr, "pullRequest", "review", JsonValueKind.String).GetString()!, "review"),
            now);
    }

    private static WorkProgress? Progress(JsonElement root)
    {
        if (Member(root, "progress", ProgressFields) is not { } progress) return null;
        var resolved = Count(progress, "resolved");
        var total = Count(progress, "total");
        return resolved <= total
            ? new WorkProgress(resolved, total)
            : throw new FormatException($"progress.resolved {resolved} is more than progress.total {total}");
    }

    /// <summary>A whole number of at least 0.</summary>
    private static int Count(JsonElement progress, string name) =>
        Required(progress, "progress", name, JsonValueKind.Number).TryGetInt32(out var count) && count >= 0
            ? count
            : throw new FormatException($"progress.{name} is not a whole number of at least 0");

    /// <summary>The object <paramref name="name"/> of the output, or null when it is missing or null; refused when it is anything else, or has a property not in <paramref name="fields"/>.</summary>
    private static JsonElement? Member(JsonElement root, string name, string[] fields)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException($"{name} is a JSON {value.ValueKind}, not an object");
        if (Unknown(value, fields) is { } unknownField)
            throw new FormatException($"unknown property '{name}.{Cut(unknownField)}'");
        return value;
    }

    /// <summary>
    /// The status to keep once a check reported <paramref name="reported"/>: the previous one, with
    /// its <see cref="PullRequestStatus.ChangedAt"/>, when the same pull request is in the same state
    /// and review; otherwise what was reported.
    /// </summary>
    public static PullRequestStatus? Apply(PullRequestStatus? previous, PullRequestStatus? reported) =>
        previous is { } before && reported is { } after
            && (before.Number, before.State, before.Review) == (after.Number, after.State, after.Review)
            ? after with { ChangedAt = before.ChangedAt }
            : reported;

    private static string? Unknown(JsonElement obj, string[] known) =>
        obj.EnumerateObject().Select(p => p.Name).FirstOrDefault(name => !known.Contains(name));

    private static JsonElement Required(JsonElement obj, string parent, string name, JsonValueKind kind) =>
        !obj.TryGetProperty(name, out var value) ? throw new FormatException($"{parent}.{name} is missing")
        : value.ValueKind != kind ? throw new FormatException($"{parent}.{name} is a JSON {value.ValueKind}, not a {kind}")
        : value;

    /// <summary>
    /// Whether <paramref name="url"/> is a pull request link the server keeps: an absolute http(s)
    /// URL of at most <see cref="MaxUrlLength"/> characters. Also what a recovered status.json must hold.
    /// </summary>
    public static bool IsValidUrl(string? url) =>
        url is { Length: <= MaxUrlLength } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    private static string Url(string url) =>
        IsValidUrl(url)
            ? url
            : throw new FormatException($"pullRequest.url '{Cut(url)}' is not an http(s) URL of at most {MaxUrlLength} characters");

    private static T OneOf<T>(Dictionary<string, T> values, string value, string name) =>
        values.TryGetValue(value, out var parsed)
            ? parsed
            : throw new FormatException($"pullRequest.{name} '{Cut(value)}' is not one of {string.Join(", ", values.Keys)}");

    /// <summary>A piece of the output, short enough to log.</summary>
    private static string Cut(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
