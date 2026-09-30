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
/// <param name="AllowSkipPermissions">
/// Whether a project of this action may run with <c>--dangerously-skip-permissions</c>: a create may
/// ask for it, and a launch honours what the project's settings ask. Off unless the root turns it on.
/// </param>
/// <param name="PermissionMode">
/// The claude permission mode (<c>--permission-mode</c>) a project of this action is created with,
/// as claude spells it; null for claude's own default. Never <c>bypassPermissions</c>.
/// </param>
/// <param name="SharedFolder">
/// Whether sessions of this action share their working folder (an assistant's workspace): a create
/// may then go into a folder other sessions use, if they share it too, and a delete removes only the
/// session's state (<c>.godmode/sessions/{id}/</c>), never the folder. Off: a folder another session
/// uses is refused, and a delete removes the folder.
/// </param>
/// <param name="Session">
/// Whether the action starts a session. Off (<c>"session": false</c>): its prepare and create scripts
/// run in the root, as a create's do, and nothing more: no folder, no session, no claude. Such an
/// action provisions something on the host (a new root, say), and its result file's <c>message</c>
/// says what it made.
/// </param>
/// <param name="Transient">
/// Whether the action's sessions are short-lived (chats, experiments): the app folds them away from
/// its list sooner. Nothing is deleted by it. Set in <c>config.json</c> for every action of the root,
/// or in an action's overlay for that action.
/// </param>
/// <param name="Adopt">
/// Whether the action's create script knows how to adopt a folder that exists (<c>"adopt": true</c>): an
/// adopt of a folder with it runs the create script alone, with <c>GODMODE_ADOPT=true</c> and the folder
/// as the project's, to name the session and nothing more. An adopt with any other action runs no script.
/// </param>
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
    string ResumePrompt = CreateAction.DefaultResumePrompt,
    bool AllowSkipPermissions = false,
    string? PermissionMode = null,
    bool SharedFolder = false,
    bool Session = true,
    bool Transient = false,
    bool Adopt = false
)
{
    public const string DefaultResumePrompt = "The GodMode server restarted and interrupted you. Continue where you left off.";
}
