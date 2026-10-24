namespace GodMode.Shared.Models;

/// <summary>What a delete did with the session's files.</summary>
/// <param name="Trashed">
/// The session shared its working folder: only its state went, moved to the folder's
/// <c>.godmode/trash/</c>, and <see cref="Hubs.IProjectHub.RestoreProject"/> brings it back until the
/// trash is purged. False when the delete removed the working folder, which nothing brings back.
/// </param>
/// <param name="Left">
/// What the delete left on disk (issue #599), when the session had left the list and its folder could not be removed (a
/// file held open, say) or its state not moved to the trash: what stays, and why. The session is deleted all the same,
/// and <see cref="Hubs.IProjectHubClient.ProjectDeleted"/> is sent. Null when every file went as it should.
/// </param>
public record DeleteProjectResult(bool Trashed, string? Left = null);
