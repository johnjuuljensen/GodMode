using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// An overseer's delete of a worker it is done with (issue #428): the user's delete, through the same path, narrowed to
/// what an overseer may take down. Its own descendants alone, by the parent the server recorded at each create
/// (<see cref="FleetGrantFile.Grant.Parent"/>), never a <c>status.json</c>'s <c>ParentId</c>, which a session can write.
/// Never forced: the root's delete script decides, as it does for the user, and its refusal is the caller's error.
/// </summary>
public partial class ProjectManager
{
    public async Task<DeleteProjectResult> DeleteChildAsync(string callerId, string projectId)
    {
        RefuseDeleting(projectId);
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");
        if (string.Equals(projectId, callerId, StringComparison.Ordinal))
            throw new InvalidOperationException("A session does not delete itself: the user deletes it.");
        if (!IsDescendantOf(project, callerId))
            throw new InvalidOperationException($"Session {projectId} is not one you started, nor one your children started: a session deletes its own children alone.");

        // A pull request merged since the last check (by the overseer, with gh) makes no transition of an Idle worker,
        // so the poll would see it only after its interval: a refusal over an open one checks it now (issue #609)
        var checkedAt = WhyNotDoneButPullRequest(project) == null && project.Status.PullRequest is { IsOpen: true }
            ? await CheckPullRequestNowAsync(project)
            : null;

        // Checked under the lock the delete takes, so no launch comes between the check and the delete
        return await WhileDeletingAsync(project, () => WithTrackedLockAsync(project, () => WhyNotDone(project, checkedAt) is { } why
            ? throw new InvalidOperationException($"Session {projectId} is not deleted: {why}.")
            : DeleteLockedAsync(project, force: false)));
    }

    /// <summary>Whether <paramref name="ancestorId"/> started the session, or started the session that did, and so on up.</summary>
    private bool IsDescendantOf(ProjectInfo project, string ancestorId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var parentId = ServerParentOf(project); parentId != null && seen.Add(parentId);
             parentId = _projects.TryGetValue(parentId, out var parent) ? ServerParentOf(parent) : null)
            if (string.Equals(parentId, ancestorId, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Runs the project's status check now, one at a time with the poll's, and says when it read the pull request;
    /// null when it failed, and the stored reading stands.
    /// </summary>
    private async Task<DateTime?> CheckPullRequestNowAsync(ProjectInfo project)
    {
        var asked = DateTime.UtcNow;
        await _pullRequests.CheckAsync(project.Status.Id);
        return project.PullRequestReadAt is { } readAt && readAt >= asked ? readAt : null;
    }

    /// <summary>
    /// Why the session is not done with, for an overseer's delete; null when it is. An open pull request is named with
    /// when it was read: <paramref name="checkedAt"/>, the check the delete just ran, else when it was first seen open.
    /// </summary>
    private string? WhyNotDone(ProjectInfo project, DateTime? checkedAt) =>
        WhyNotDoneButPullRequest(project) ?? (project.Status.PullRequest is { IsOpen: true } pr
            ? $"its pull request {pr.Url} is {pr.State.ToString().ToLowerInvariant()}, "
              + (checkedAt is { } at
                  ? $"as its status check read it just now ({at:yyyy-MM-ddTHH:mm:ssZ})"
                  : $"as read at {pr.ChangedAt:yyyy-MM-ddTHH:mm:ssZ}, when it was first seen so (a check just now failed)")
              + "; delete it once the pull request is merged or closed"
            : null);

    /// <summary>Why the session is not done with, but for its pull request.</summary>
    private string? WhyNotDoneButPullRequest(ProjectInfo project)
    {
        var status = project.Status;
        // An AskUserQuestion reaches the server as a permission prompt: the question first
        if (status is { PendingQuestion: not null } or { State: ProjectState.WaitingInput })
            return "it waits on a question to the user";
        if (status is { PendingPermission: not null } or { State: ProjectState.WaitingPermission })
            return "it waits on a permission prompt, which is the user's";
        if (status.State == ProjectState.Running)
            return "it is running; delete it once its turn has ended";
        var children = _projects.Values
            .Where(other => !ReferenceEquals(other, project) && ServerParentOf(other) == status.Id)
            .Select(other => other.Status.Id).Order(StringComparer.Ordinal).ToArray();
        return children.Length > 0 ? $"it has sessions of its own ({string.Join(", ", children)}); delete them first" : null;
    }
}
