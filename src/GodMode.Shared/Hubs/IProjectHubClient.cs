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
    /// this file (another generation, or past its end). Each later batch starts where the previous one ended.
    /// </summary>
    /// <param name="subscriptionId">The <see cref="IProjectHub.SubscribeProject"/> this answers.</param>
    /// <param name="generation">The output generation the offsets are in: a new one each time the
    /// project is created, so an ID deleted and created again starts a new one.</param>
    Task OutputBatch(string projectId, string subscriptionId, string generation, long fromOffset, IReadOnlyList<OutputLine> lines);

    /// <summary>
    /// The replay for a subscription is done, at offset; live <see cref="OutputReceived"/> lines
    /// follow from there, with none missed or repeated. A generation other than the one the client
    /// holds, or an offset lower than the one asked for (output.jsonl is shorter than the client
    /// thought), means what it holds is not from this file.
    /// </summary>
    /// <param name="subscriptionId">The <see cref="IProjectHub.SubscribeProject"/> this answers.</param>
    /// <param name="generation">The output generation <paramref name="offset"/> is in, as in <see cref="OutputBatch"/>.</param>
    Task OutputReplayComplete(string projectId, string subscriptionId, string generation, long offset);

    /// <summary>
    /// Called when a project's status changes.
    /// </summary>
    Task StatusChanged(string projectId, ProjectStatus status);

    /// <summary>
    /// The projects that need the user changed: <paramref name="items"/> is the whole list, as
    /// <see cref="IProjectHub.GetAttention"/> returns it. Pushed only when the list differs from
    /// the last one pushed, not on every status change.
    /// </summary>
    Task AttentionChanged(AttentionItem[] items);

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

}
