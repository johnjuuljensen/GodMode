using System.Text.Json;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// What a root's <c>status</c> script prints, read strictly: the output is untrusted, so anything
/// but the documented object is refused rather than guessed at.
/// <code>
/// {"pullRequest": {"url": "https://…", "number": 12, "state": "draft|open|merged|closed", "review": "none|changes_requested|approved"}}
/// </code>
/// or <c>{}</c> (or <c>{"pullRequest": null}</c>) when there is no pull request.
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

    /// <summary>
    /// The pull request the output reports, seen at <paramref name="now"/>, or null for none.
    /// Throws <see cref="FormatException"/> saying what is wrong with anything else.
    /// </summary>
    public static PullRequestStatus? Parse(string output, DateTime now)
    {
        if (output.Length > MaxOutputChars) throw new FormatException($"the output is longer than {MaxOutputChars} characters");

        JsonDocument document;
        try { document = JsonDocument.Parse(output, Strict); }
        catch (JsonException ex) { throw new FormatException($"the output is not one JSON object: {ex.Message}", ex); }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException($"the output is a JSON {root.ValueKind}, not an object");
            if (root.EnumerateObject().FirstOrDefault(p => p.Name != "pullRequest") is { Name: { } unknown })
                throw new FormatException($"unknown property '{Cut(unknown)}'");
            if (!root.TryGetProperty("pullRequest", out var pr) || pr.ValueKind == JsonValueKind.Null) return null;
            if (pr.ValueKind != JsonValueKind.Object) throw new FormatException($"pullRequest is a JSON {pr.ValueKind}, not an object");
            if (pr.EnumerateObject().FirstOrDefault(p => !Fields.Contains(p.Name)) is { Name: { } unknownField })
                throw new FormatException($"unknown property 'pullRequest.{Cut(unknownField)}'");

            return new PullRequestStatus(
                Url(Required(pr, "url", JsonValueKind.String).GetString()!),
                Required(pr, "number", JsonValueKind.Number).TryGetInt32(out var number) && number > 0
                    ? number
                    : throw new FormatException("pullRequest.number is not a positive whole number"),
                OneOf(States, Required(pr, "state", JsonValueKind.String).GetString()!, "state"),
                OneOf(Reviews, Required(pr, "review", JsonValueKind.String).GetString()!, "review"),
                now);
        }
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

    private static JsonElement Required(JsonElement pr, string name, JsonValueKind kind) =>
        !pr.TryGetProperty(name, out var value) ? throw new FormatException($"pullRequest.{name} is missing")
        : value.ValueKind != kind ? throw new FormatException($"pullRequest.{name} is a JSON {value.ValueKind}, not a {kind}")
        : value;

    private static string Url(string url) =>
        url.Length <= MaxUrlLength && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? url
            : throw new FormatException($"pullRequest.url '{Cut(url)}' is not an http(s) URL of at most {MaxUrlLength} characters");

    private static T OneOf<T>(Dictionary<string, T> values, string value, string name) =>
        values.TryGetValue(value, out var parsed)
            ? parsed
            : throw new FormatException($"pullRequest.{name} '{Cut(value)}' is not one of {string.Join(", ", values.Keys)}");

    /// <summary>A piece of the output, short enough to log.</summary>
    private static string Cut(string text) => text.Length <= 80 ? text : text[..80] + "…";
}
