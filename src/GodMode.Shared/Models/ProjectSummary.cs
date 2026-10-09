using System.Text.Json.Serialization;
using GodMode.Shared.Enums;

namespace GodMode.Shared.Models;

/// <summary>
/// Summary information about a project.
/// </summary>
/// <param name="Id">The session's identifier, <c>{profile}/{root}/{id}</c>, as in <see cref="ProjectStatus.Id"/>. Opaque to clients, which pass it back as received; not the folder name.</param>
/// <param name="Name">The project name.</param>
/// <param name="State">The current state of the project.</param>
/// <param name="UpdatedAt">The timestamp when the project was last updated.</param>
/// <param name="CurrentQuestion">The current question waiting for input, if any.</param>
/// <param name="PendingPermission">The tool call waiting to be allowed or denied, as in <see cref="ProjectStatus.PendingPermission"/>.</param>
/// <param name="PendingQuestion">The AskUserQuestion waiting for an answer, as in <see cref="ProjectStatus.PendingQuestion"/>.</param>
/// <param name="PullRequest">The project's pull request, as in <see cref="ProjectStatus.PullRequest"/>.</param>
/// <param name="Kind">The session's kind, its label, as in <see cref="ProjectStatus.Kind"/>.</param>
/// <param name="ActionName">The session's action, as in <see cref="ProjectStatus.ActionName"/>.</param>
/// <param name="SharedFolder">Whether its delete removes only its state, as in <see cref="ProjectStatus.SharedFolder"/>.</param>
/// <param name="Adopted">Whether the session was adopted, as in <see cref="ProjectStatus.Adopted"/>: the app offers Forget beside its delete.</param>
/// <param name="ParentId">The session that started this one, or null, as in <see cref="ProjectStatus.ParentId"/>.</param>
/// <param name="SlashCommands">The slash commands GodMode sends to the session, as in <see cref="ProjectStatus.SlashCommands"/>.</param>
/// <param name="RecordedParentId">
/// The session that started this one as the server recorded it, as in <see cref="AttentionItem.RecordedParentId"/>:
/// the overseer that runs it. Null for a top-level session.
/// </param>
/// <param name="Importance">How much the session may interrupt the user, as in <see cref="ProjectStatus.Importance"/>.</param>
/// <param name="Recap">The session's one-line recap of where it stands, as in <see cref="ProjectStatus.Recap"/>.</param>
/// <param name="RecapAt">When the session last gave its recap, as in <see cref="ProjectStatus.RecapAt"/>.</param>
/// <param name="Outcome">What the session's last turn's end counts as, as in <see cref="ProjectStatus.EffectiveOutcome"/>: done once its pull request is merged.</param>
/// <param name="LastResultAt">When the session's last turn ended, as in <see cref="ProjectStatus.LastResultAt"/>.</param>
/// <param name="LastOutputAt">When its main conversation last wrote a line, as in <see cref="ProjectStatus.LastOutputAt"/> (issue #468).</param>
/// <param name="BackgroundTasks">What it runs in the background, or null for nothing, as in <see cref="ProjectStatus.BackgroundTasks"/> (issue #432).</param>
public record ProjectSummary(
    string Id,
    string Name,
    ProjectState State,
    DateTime UpdatedAt,
    string? CurrentQuestion = null,
    string? RootName = null,
    string? ProfileName = null,
    PendingPermission? PendingPermission = null,
    PendingQuestion? PendingQuestion = null,
    PullRequestStatus? PullRequest = null,
    string? Kind = null,
    string? ActionName = null,
    bool SharedFolder = false,
    bool Adopted = false,
    string? ParentId = null,
    IReadOnlyList<string>? SlashCommands = null,
    string? RecordedParentId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Importance Importance = Importance.Normal,
    string? Recap = null,
    DateTime? RecapAt = null,
    TurnOutcome? Outcome = null,
    DateTime? LastResultAt = null,
    DateTime? LastOutputAt = null,
    IReadOnlyList<BackgroundTask>? BackgroundTasks = null
)
{
    /// <summary>
    /// The summary of <paramref name="status"/>, as the status has it: the server's list sets what it knows better (the
    /// profile it holds the session in, the parent it recorded), and a client makes one of each status it hears.
    /// </summary>
    public static ProjectSummary Of(ProjectStatus status) => new(
        status.Id,
        status.Name,
        status.State,
        status.UpdatedAt,
        status.CurrentQuestion,
        status.RootName,
        status.ProfileName,
        status.PendingPermission,
        status.PendingQuestion,
        status.PullRequest,
        status.Kind,
        status.ActionName,
        status.SharedFolder,
        status.Adopted,
        status.ParentId,
        status.SlashCommands,
        Importance: status.Importance,
        Recap: status.Recap,
        RecapAt: status.RecapAt,
        Outcome: status.EffectiveOutcome,
        LastResultAt: status.LastResultAt,
        LastOutputAt: status.LastOutputAt,
        BackgroundTasks: status.BackgroundTasks);
}
