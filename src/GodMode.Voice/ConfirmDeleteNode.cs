using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The yes a delete waits on (<see cref="SessionDeletes.Armed"/>, #532), as <see cref="ConfirmCreateNode"/> is a
/// create's: the final of the first utterance the user starts after the read-back started playing is the answer, and
/// only a clear yes (<see cref="ConfirmCreateNode.Yes"/>) deletes. A plain no, a yes queued before the read-back was
/// heard, or its echo cancels it and says so. Anything else ("Nej, kun 525") cancels it too, and goes on to the chat with
/// the read-back it answers, which may propose another delete. A delete that waits no more takes no yes: a yes then is
/// told there is nothing to confirm. The model never deletes: the graph has no tool that does. A stop read back (#287)
/// is answered the same way: only the yes stops.
/// </summary>
public sealed class ConfirmDeleteNode(string id, int priority, SessionDeletes deletes, VoicePhrases phrases) : INode
{
    public string Id => id;
    public int Priority => priority;

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { } transcription)
        {
            _ = deletes.Armed;   // a tick: one that has waited too long is dropped on time
            return Task.FromResult<NodeResult?>(null);
        }

        // When the utterance started: its first partial, or the final itself when it came alone (typed text)
        var started = context.GraphState.Get<DateTimeOffset?>(context.StateKey, null);
        if (transcription.IsPartial)
        {
            if (started is null) context.GraphState.Set(context.StateKey, transcription.Timestamp);
            return Task.FromResult<NodeResult?>(null);
        }
        context.GraphState.Remove(context.StateKey);
        started ??= transcription.Timestamp;

        var text = context.CleanedText ?? transcription.Text;
        string said;
        if (deletes.Armed is { } armed)
        {
            var stop = armed.Request.Action == SessionAction.Stop;
            if (started >= armed.At && ConfirmCreateNode.IsChangeTo(text, armed.ReadBack) && deletes.Cancel() is { } changed)
            {
                context.Log?.Log("DELETE", $"'{text}' → change, to the chat: {changed.Action} {changed.What}");
                context.CleanedText = $"{text}\n[The user's words answer the read-back \"{armed.ReadBack}\". Nothing was " +
                    $"{(stop ? "stopped" : "deleted")}, and it waits on no yes. If they change which sessions to " +
                    $"{(stop ? "stop" : "delete")}, call {(stop ? VoiceTools.StopProject : VoiceTools.DeleteSession)} again with them: " +
                    "it is read back again. If they only decline it, say it was cancelled.]";
                return Task.FromResult<NodeResult?>(null);
            }
            var yes = started >= armed.At && ConfirmCreateNode.IsYesTo(text, armed.ReadBack);
            var request = yes ? deletes.Confirm(armed) : deletes.Cancel();
            if (request is null)
                return Task.FromResult<NodeResult?>(null);
            said = yes ? phrases.Confirmed(request.Action) : phrases.Cancelled(request.Action);
            context.Log?.Log("DELETE", $"'{text}' → {(yes ? request.Action.ToString().ToLowerInvariant() : "cancel")} {request.What}");
        }
        else if (deletes.TakeDropped() is { } dropped && ConfirmCreateNode.IsYes(text))
        {
            said = phrases.NothingWaits(dropped);
            context.Log?.Log("DELETE", $"'{text}' → nothing waits on a yes");
        }
        else
            return Task.FromResult<NodeResult?>(null);

        context.AddUserMessage(text);
        context.AddAssistantMessage(said);
        return Task.FromResult<NodeResult?>(new NodeResult { ResponseText = said });
    }
}
