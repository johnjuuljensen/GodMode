namespace GodMode.Shared.Models;

/// <summary>
/// What claude said in one turn of a session, from its <c>output.jsonl</c>: the server's <c>OutputLog.LastRepliesAsync</c>,
/// which the hub's <see cref="Hubs.IProjectHub.GetLastReplies"/> and the fleet's <c>read</c> tool give.
/// </summary>
/// <param name="Text">
/// The turn's last assistant message with text (its text blocks, joined by a blank line): claude's reply,
/// as its <c>result</c> line repeats it. A finished turn with no assistant text has its result's text,
/// which may be empty.
/// </param>
/// <param name="Finished">Whether the turn has its <c>result</c> line. The last turn is unfinished while claude works on it, or when it was stopped in the middle.</param>
/// <param name="IsError">Whether the turn's result is an error (<c>is_error</c>).</param>
public sealed record AssistantReply(string Text, bool Finished, bool IsError = false);
