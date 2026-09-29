namespace GodMode.Shared.Models;

/// <summary>What a delete did with the session's files.</summary>
/// <param name="Trashed">
/// The session shared its working folder: only its state went, moved to the folder's
/// <c>.godmode/trash/</c>, and <see cref="Hubs.IProjectHub.RestoreProject"/> brings it back until the
/// trash is purged. False when the delete removed the working folder, which nothing brings back.
/// </param>
public record DeleteProjectResult(bool Trashed);
