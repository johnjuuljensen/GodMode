namespace GodMode.Shared.Models;

/// <summary>
/// Resolved configuration for a project root directory.
/// Built by RootConfigReader from multi-file discovery and merging:
/// config.json (base) + config.{action}.json (per-action overlays).
/// All merging logic lives in RootConfigReader — this is the final resolved output.
/// </summary>
public record RootConfig(
    string? Description = null,
    IReadOnlyDictionary<string, CreateAction>? Actions = null,
    string? ProfileName = null)
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
