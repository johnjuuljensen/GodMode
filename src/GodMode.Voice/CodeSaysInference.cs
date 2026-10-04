using Microsoft.Extensions.AI;
using VoiceBot.Core.AI;

namespace GodMode.Voice;

/// <summary>
/// The chat node's model, but for the round after a tool whose result the code says itself (#456): what needs me, the
/// projects, a project's question or result (<see cref="VoiceConversation.SaysItself"/>). That round is answered with
/// <c>respond</c> and the code's words (<see cref="VoicePhrases"/>), and the model is not called: it picked the tool and
/// its arguments, and its retelling of the result would add nothing but a second call's wait. A result the code says
/// nothing for (an error, an unknown project, a text too long or too marked up to say as it is) goes to the model, as
/// every result did before.
/// </summary>
public sealed class CodeSaysInference(IInferenceProvider model, VoiceConversation conversation) : IInferenceProvider
{
    public IChatClient GetClient(InferenceTier tier) => model.GetClient(tier);

    public Task<ChatResponse> CompleteAsync(InferenceTier tier, IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        messages is [.., { Role: var role, Contents: [FunctionResultContent { Result: string result }] }] && role == ChatRole.Tool
        && conversation.TakeSaid(result) is { } said
            ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"said-by-code-{Guid.NewGuid():N}", "respond", new Dictionary<string, object?> { ["response_text"] = said })])))
            : model.CompleteAsync(tier, messages, options, ct);
}
