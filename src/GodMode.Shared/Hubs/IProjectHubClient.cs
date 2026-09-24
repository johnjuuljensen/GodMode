using GodMode.Shared.Models;

namespace GodMode.Shared.Hubs;

/// <summary>
/// Interface for SignalR client methods that the server can invoke.
/// Used with Hub&lt;IProjectHubClient&gt; for strongly-typed hub communication.
/// </summary>
public interface IProjectHubClient
{
    /// <summary>
    /// A line of live output from a project's Claude process, sent to the connections subscribed
    /// to it once their replay is complete. The rawJson is the raw JSON line from Claude's
    /// --output-format stream-json; offset is the byte offset in output.jsonl just after it.
    /// </summary>
    Task OutputReceived(string projectId, long offset, string rawJson);

    /// <summary>
    /// Replayed output, in order, to the connection that subscribed. The batch covers output.jsonl
    /// from fromOffset to its last line's offset. The first batch's fromOffset is where the replay
    /// starts: the offset asked for, except for the last turns, or 0 when that offset is not from
    /// this file. Each later batch starts where the previous one ended.
    /// </summary>
    Task OutputBatch(string projectId, long fromOffset, IReadOnlyList<OutputLine> lines);

    /// <summary>
    /// The replay for a subscription is done, at offset; live <see cref="OutputReceived"/> lines
    /// follow from there, with none missed or repeated. An offset lower than the one asked for means
    /// output.jsonl is shorter than the client thought: what it holds is not from this file.
    /// </summary>
    Task OutputReplayComplete(string projectId, long offset);

    /// <summary>
    /// Called when a project's status changes.
    /// </summary>
    Task StatusChanged(string projectId, ProjectStatus status);

    /// <summary>
    /// Called when a new project is created.
    /// </summary>
    Task ProjectCreated(ProjectStatus status);

    /// <summary>
    /// Called during project creation to stream script progress to the client.
    /// </summary>
    Task CreationProgress(string projectId, string message);

    /// <summary>
    /// Called when a project is deleted.
    /// </summary>
    Task ProjectDeleted(string projectId);

    /// <summary>
    /// Called when a project is archived.
    /// </summary>
    Task ProjectArchived(string projectId);

    /// <summary>
    /// Called when a project is restored from archive.
    /// </summary>
    Task ProjectRestored(ProjectSummary project);

    /// <summary>
    /// Called when profiles change (created, updated, or deleted).
    /// Clients should refresh their profile list.
    /// </summary>
    Task ProfilesChanged();
}
