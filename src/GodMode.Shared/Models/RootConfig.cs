using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// Configuration for a project root directory, read from .godmode-root/config.json
/// (falls back to legacy .godmode-root.json).
/// Supports multiple named create actions via the Actions array.
/// When Actions is not set, root-level fields define a single implicit "Create" action (backward compat).
/// Root-level Environment, ClaudeArgs, and ClaudeConfigDir serve as shared defaults
/// that are merged with each action's own values when Actions is explicitly set.
/// </summary>
public record RootConfig(
    string? Description = null,
    CreateAction[]? Actions = null,
    // Shared config (merged with action-level when Actions is set)
    Dictionary<string, string>? Environment = null,
    string[]? ClaudeArgs = null,
    string? ClaudeConfigDir = null,
    // Legacy single-action fields (used when Actions is null for backward compat)
    JsonElement? InputSchema = null,
    string[]? Setup = null,
    string[]? Bootstrap = null,
    string[]? Teardown = null,
    string? NameTemplate = null,
    string? PromptTemplate = null,
    bool ScriptsCreateFolder = false
)
{
    /// <summary>
    /// Returns the effective list of create actions.
    /// If Actions is set, returns those. Otherwise creates a single "Create" action
    /// from the legacy root-level fields.
    /// </summary>
    public CreateAction[] GetEffectiveActions() =>
        Actions is { Length: > 0 }
            ? Actions
            :
            [
                new CreateAction(
                    "Create",
                    Description,
                    InputSchema,
                    Setup, Bootstrap, Teardown,
                    Environment, ClaudeArgs,
                    NameTemplate, PromptTemplate,
                    ClaudeConfigDir, ScriptsCreateFolder)
            ];

    /// <summary>
    /// Resolves a specific action by name, merging root-level shared config with action-level config.
    /// Returns null if the action is not found.
    /// When actionName is null, returns the first (or only) action.
    /// </summary>
    public CreateAction? ResolveAction(string? actionName)
    {
        var actions = GetEffectiveActions();
        var action = actionName != null
            ? Array.Find(actions, a => string.Equals(a.Name, actionName, StringComparison.OrdinalIgnoreCase))
            : actions.Length > 0 ? actions[0] : null;

        if (action == null) return null;

        // When using explicit Actions, merge root-level shared config with action-level
        if (Actions is { Length: > 0 })
        {
            return action with
            {
                Environment = MergeDictionaries(Environment, action.Environment),
                ClaudeArgs = MergeArrays(ClaudeArgs, action.ClaudeArgs),
                ClaudeConfigDir = action.ClaudeConfigDir ?? ClaudeConfigDir
            };
        }

        // Legacy mode: action already has all config from root-level fields
        return action;
    }

    private static Dictionary<string, string>? MergeDictionaries(
        Dictionary<string, string>? baseDict, Dictionary<string, string>? overrideDict)
    {
        if (baseDict == null) return overrideDict;
        if (overrideDict == null) return baseDict;

        var merged = new Dictionary<string, string>(baseDict);
        foreach (var (key, value) in overrideDict)
            merged[key] = value;
        return merged;
    }

    private static string[]? MergeArrays(string[]? baseArr, string[]? additionalArr)
    {
        if (baseArr == null) return additionalArr;
        if (additionalArr == null) return baseArr;
        return [.. baseArr, .. additionalArr];
    }
}
