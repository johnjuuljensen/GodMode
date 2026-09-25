using System.Text.Json;

namespace GodMode.Shared.Models;

/// <summary>
/// A named create action within a project root.
/// Each action defines its own input schema, scripts, templates, and configuration.
/// </summary>
/// <param name="Status">
/// The script that reports the project's pull request, run in the project folder: it prints one JSON
/// object, <c>{"pullRequest": {...}}</c>, or <c>{}</c> when there is none. Null when the root has none.
/// </param>
/// <param name="ResumeOnRestart">Whether a project that was active when the server stopped is resumed when it starts again.</param>
/// <param name="ResumePrompt">What a project that was working when the server stopped is told when it is resumed.</param>
public record CreateAction(
    string Name,
    string? Description = null,
    JsonElement? InputSchema = null,
    string[]? Prepare = null,
    string[]? Create = null,
    string[]? Delete = null,
    Dictionary<string, string>? Environment = null,
    string[]? ClaudeArgs = null,
    string? NameTemplate = null,
    string? PromptTemplate = null,
    bool ScriptsCreateFolder = false,
    string? Model = null,
    string? Status = null,
    bool ResumeOnRestart = true,
    string ResumePrompt = CreateAction.DefaultResumePrompt
)
{
    public const string DefaultResumePrompt = "The GodMode server restarted and interrupted you. Continue where you left off.";
}
