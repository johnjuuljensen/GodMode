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
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");
        if (string.Equals(projectId, callerId, StringComparison.Ordinal))
            throw new InvalidOperationException("A session does not delete itself: the user deletes it.");
        if (!IsDescendantOf(project, callerId))
            throw new InvalidOperationException($"Session {projectId} is not one you started, nor one your children started: a session deletes its own children alone.");

        // Checked under the lock the delete takes, so no launch comes between the check and the delete
        return await WithTrackedLockAsync(project, () => WhyNotDone(project) is { } why
            ? throw new InvalidOperationException($"Session {projectId} is not deleted: {why}.")
            : DeleteLockedAsync(project, force: false));
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

    /// <summary>Why the session is not done with, for an overseer's delete; null when it is.</summary>
    private string? WhyNotDone(ProjectInfo project)
    {
        var status = project.Status;
        if (status is { PendingPermission: not null } or { State: ProjectState.WaitingPermission })
            return "it waits on a permission prompt, which is the user's";
        if (status is { PendingQuestion: not null } or { State: ProjectState.WaitingInput })
            return "it waits on a question to the user";
        if (status.State == ProjectState.Running)
            return "it is running; delete it once its turn has ended";
        var children = _projects.Values
            .Where(other => !ReferenceEquals(other, project) && ServerParentOf(other) == status.Id)
            .Select(other => other.Status.Id).Order(StringComparer.Ordinal).ToArray();
        if (children.Length > 0)
            return $"it has sessions of its own ({string.Join(", ", children)}); delete them first";
        if (status.PullRequest is { IsOpen: true } pr)
            return $"its pull request {pr.Url} is {pr.State.ToString().ToLowerInvariant()}; delete it once the pull request is merged or closed";
        return null;
    }
}
