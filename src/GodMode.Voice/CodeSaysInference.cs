using System.Text.Json;
using Microsoft.Extensions.AI;
using VoiceBot.Core.AI;
using VoiceBot.Core.Graph.Nodes;

namespace GodMode.Voice;

/// <summary>
/// The chat node's model, but for the round after a tool whose result the code says itself (#456): what needs me, the
/// projects, a project's question or result (<see cref="VoiceConversation.SaysItself"/>). That round is answered with
/// <c>respond</c> and the code's words (<see cref="VoicePhrases"/>), and the model is not called: it picked the tool and
/// its arguments, and its retelling of the result would add nothing but a second call's wait. A result the code says
/// nothing for (an error, an unknown project, a text too long or too marked up to say as it is) goes to the model, as
/// every result did before.
/// A call the user asked for more after, in the same breath (its <see cref="VoiceTools.ThenParameter"/>, #507: "hvad
/// venter, og læs 283"), does not end the turn: the code's words are held, the model is called for the rest, and the
/// reply it ends on, its own or the code's, is said after them.
/// </summary>
/// The model's own words after held ones are checked for a claimed send as <see cref="SentNode"/> checks a reply (#375):
/// behind the code's words its "Sendt" no longer starts the reply, where SentNode looks for it.
public sealed class CodeSaysInference(IInferenceProvider model, VoiceConversation conversation, VoicePhrases? phrases = null) : IInferenceProvider
{
    /// <summary>The code's words for the calls before this one in the turn, said before its reply; null when none waits.</summary>
    private string? _held;

    public IChatClient GetClient(InferenceTier tier) => model.GetClient(tier);

    public async Task<ChatResponse> CompleteAsync(InferenceTier tier, IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        // A turn's first round: nothing of an earlier one is held
        if (messages is [.., { Role: var first }] && first == ChatRole.User)
            Volatile.Write(ref _held, null);
        if (messages is not [.., { Role: var role, Contents: [FunctionResultContent { Result: string result } content] }] || role != ChatRole.Tool
            || conversation.TakeSaidByCode(result) is not { } said)
            return HeldBefore(await model.CompleteAsync(tier, messages, options, ct));

        var held = Joined(Volatile.Read(ref _held), said.Said);
        if (said.Then is not { } then)
        {
            Volatile.Write(ref _held, null);
            return Respond(held);
        }

        // More was asked for: the model makes the next call, knowing what was said
        Volatile.Write(ref _held, held);
        List<ChatMessage> rest = [.. messages.Take(messages.Count - 1), new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(content.CallId, $"{result}\nThe system has said this to the user itself already: \"{said.Said}\". " +
                $"Go on with what the user asked for after it, \"{then}\": call its tool now. Respond only with what you add, never this again.")])];
        return HeldBefore(await model.CompleteAsync(tier, rest, options, ct));
    }

    /// <summary>
    /// The model's response, with the code's words held for the turn, if any, said before its answer's reply: or alone,
    /// when it ends the turn any other way (no answer, or waiting), so they are never lost. A tool call goes on as it is.
    /// </summary>
    private ChatResponse HeldBefore(ChatResponse response)
    {
        if (Volatile.Read(ref _held) is not { } held)
            return response;
        if (response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any())
            return response;
        Volatile.Write(ref _held, null);
        // ChatNode takes any answer but a wait or a skip for a reply
        var answer = StructuredAnswer.Read(response) is { } read && read.GetString("action") is not (ChatNode.Wait or ChatNode.AlreadyResponded)
            ? read : (JsonElement?)null;
        var added = answer?.GetString("response_text");
        // A send claimed with none sent is not said (#375); one that went out is SentNode's to say, in place of the reply
        if (added is not null && SentNode.ClaimsSend(added) && !conversation.AnySent && !conversation.ReadOutSoFar.Any(r => SentNode.Repeats(added, r)))
            added = (phrases ?? new VoicePhrases(VoiceSettings.Default.Languages)).NothingSent;
        return Respond(Joined(held, Acknowledgement(added) ? null : added), answer);
    }

    /// <summary>A reply that adds nothing to what the code said: empty, or a protocol word alone ("Klar.", "Ready").</summary>
    private static bool Acknowledgement(string? reply) =>
        reply?.Trim().TrimEnd('.', '!') is not { Length: > 0 } word || Acknowledgements.Contains(word, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Acknowledgements = ["Klar", "Ready", "OK", "Okay", "Done", "Færdig"];

    private static string Joined(string? before, string? after) =>
        string.Join(" ", new[] { before, after }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!.Trim()));

    /// <summary>ChatNode's answer (<see cref="StructuredAnswer"/>) replying <paramref name="text"/>, with the model's other fields (heard_as, exit_name) kept.</summary>
    private static ChatResponse Respond(string text, JsonElement? answer = null)
    {
        var fields = answer?.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value) ?? [];
        fields["action"] = ChatNode.Respond;
        fields["response_text"] = text;
        return new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(fields)));
    }
}
