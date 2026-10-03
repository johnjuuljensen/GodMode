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
    string? RecordedParentId = null
);
