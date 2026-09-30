using VoiceBot.Core.Commands;
using VoiceBot.Core.Graph;

namespace GodMode.Voice;

/// <summary>
/// The yes a create waits on (<see cref="SessionCreates.Armed"/>). The final of the first utterance the user starts after
/// the read-back started playing is the answer, and only a clear yes creates. Anything else cancels it and says so: a no,
/// another sentence, a yes queued before the read-back was heard, or a yes that is a fragment of the read-back itself
/// (its echo). A create that waits no more (another utterance of the bot's came between, or
/// <see cref="SessionCreates.ConfirmWindow"/> passed) takes no yes: a yes then is told there is nothing to confirm. The
/// model never creates: the graph has no tool that does. Partials pass (a "ja" may go on as "ja, men i kappe"), and so
/// does anything while no create waits.
/// </summary>
public sealed class ConfirmCreateNode(string id, int priority, SessionCreates creates, VoicePhrases phrases) : INode
{
    /// <summary>A clear yes: the whole utterance, as recognized, is one of these. Not "ok": an acknowledgement, not consent.</summary>
    public static readonly IReadOnlyList<string> Yes =
    [
        "ja", "jo", "jep", "ja tak", "ja gerne", "ja gør det", "gør det", "opret", "opret den", "ja opret den", "start den", "ja start den",
        "yes", "yeah", "yep", "yes please", "do it", "go ahead", "create it", "start it",
    ];

    private static readonly IReadOnlyList<string[]> YesTokens = [.. Yes.Select(CommandResolver.Tokenize)];

    public string Id => id;
    public int Priority => priority;

    /// <summary>Whether <paramref name="said"/> is a clear yes, and not a fragment of <paramref name="readBack"/> (the bot's own words heard back).</summary>
    public static bool IsYesTo(string said, string readBack)
    {
        var tokens = CommandResolver.Tokenize(said);
        return IsYes(tokens) && !$" {string.Join(' ', CommandResolver.Tokenize(readBack))} ".Contains($" {string.Join(' ', tokens)} ", StringComparison.Ordinal);
    }

    /// <summary>Whether <paramref name="text"/> holds a clear yes anywhere: a read-back never may, or its echo could confirm it.</summary>
    public static bool HoldsYes(string text)
    {
        var tokens = CommandResolver.Tokenize(text);
        return YesTokens.Any(y => Enumerable.Range(0, Math.Max(0, tokens.Length - y.Length + 1)).Any(i => tokens.AsSpan(i, y.Length).SequenceEqual(y)));
    }

    private static bool IsYes(string[] tokens) => YesTokens.Any(y => tokens.AsSpan().SequenceEqual(y));

    public Task<NodeResult?> EvaluateAsync(NodeContext context, CancellationToken ct)
    {
        if (context.LatestTranscription is not { } transcription)
        {
            _ = creates.Armed;   // a tick: one that has waited too long is dropped on time
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
        if (creates.Armed is { } armed)
        {
            var yes = started >= armed.At && IsYesTo(text, armed.ReadBack);
            var request = yes ? creates.Confirm(armed) : creates.Cancel();
            if (request is null)
                return Task.FromResult<NodeResult?>(null);
            said = yes ? phrases.Creating : phrases.CreateCancelled;
            context.Log?.Log("CREATE", $"'{text}' → {(yes ? "create" : "cancel")} {request.What}");
        }
        else if (creates.TakeDropped() && IsYes(CommandResolver.Tokenize(text)))
        {
            said = phrases.NothingToConfirm;
            context.Log?.Log("CREATE", $"'{text}' → nothing waits on a yes");
        }
        else
            return Task.FromResult<NodeResult?>(null);

        context.AddUserMessage(text);
        context.AddAssistantMessage(said);
        return Task.FromResult<NodeResult?>(new NodeResult { ResponseText = said });
    }
}
