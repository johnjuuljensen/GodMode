namespace GodMode.Shared.Models;

/// <summary>
/// Resolved configuration for a project root directory.
/// Built by RootConfigReader from multi-file discovery and merging:
/// config.json (base) + config.{action}.json (per-action overlays).
/// All merging logic lives in RootConfigReader — this is the final resolved output.
/// </summary>
/// <param name="List">
/// The root's <c>list</c> script (<c>config.json</c>'s, rootPath-relative), which prints the folders it
/// offers to adopt; null when it has none, and its immediate subfolders are offered.
/// </param>
/// <param name="Environment">The environment in <c>config.json</c> itself, which the <c>list</c> and <c>issueInfo</c> scripts run with; no action's overlay.</param>
/// <param name="Title">
/// What the root is shown as (<c>config.json</c>'s <c>title</c>, else its explicit entry's <c>Title</c>);
/// display only, its name stays its key. Null shows the name.
/// </param>
/// <param name="IssueInfo">
/// The root's <c>issueInfo</c> script (<c>config.json</c>'s, rootPath-relative), which prints an issue's title and
/// labels (<see cref="Models.IssueInfo"/>); null when it has none.
/// </param>
public record RootConfig(
    string? Description = null,
    IReadOnlyDictionary<string, CreateAction>? Actions = null,
    string? ProfileName = null,
    bool StripEnvVarProfile = false,
    string? List = null,
    Dictionary<string, string>? Environment = null,
    string? Title = null,
    string? IssueInfo = null)
{
    /// <summary>
    /// Resolves a specific action by name (case-insensitive).
    /// When actionName is null, returns the first (or only) action.
    /// Returns null if no actions exist or the named action is not found.
    /// </summary>
    public CreateAction? ResolveAction(string? actionName)
    {
        if (Actions is not { Count: > 0 }) return null;

        if (actionName != null)
        {
            return Actions.TryGetValue(actionName, out var action) ? action : null;
        }

        // Return first action when no name specified
        using var enumerator = Actions.Values.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : null;
    }

    /// <summary>
    /// Returns all effective create actions.
    /// </summary>
    public IEnumerable<CreateAction> GetEffectiveActions() =>
        Actions?.Values ?? [];
}
