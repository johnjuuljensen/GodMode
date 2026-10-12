using Microsoft.Extensions.AI;
using VoiceBot.Core.AI;

namespace GodMode.Voice;

/// <summary>
/// The chat node's model, told which project the app shows (#559, <see cref="VoiceConversation.Selected"/>): a line
/// after the system prompt, "ON SCREEN: issue 283, mic-timeout in GodMode", named as a list of options names it
/// (<see cref="ProjectNames.Full"/>). None when the app shows no project, or it is gone.
/// </summary>
public sealed class OnScreenInference(IInferenceProvider model, VoiceConversation conversation, ProjectNames names) : IInferenceProvider
{
    public IChatClient GetClient(InferenceTier tier) => model.GetClient(tier);

    public Task<ChatResponse> CompleteAsync(InferenceTier tier, IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        model.CompleteAsync(tier, Line() is { } line && messages is [{ Role: var role } prompt, ..] && role == ChatRole.System
            ? [new ChatMessage(ChatRole.System, $"{prompt.Text}\n\n{line}"), .. messages.Skip(1)]
            : messages, options, ct);

    /// <summary>The line, or null when the app shows no project voice knows.</summary>
    public string? Line() =>
        conversation.Selected is { } selected && names.Full(selected) is { } name
            ? $"ON SCREEN: {name}. The app shows it: a request that names no project is about it when no other was talked " +
              "about since, and \"den her\" / \"this one\" is it. Talking about another project does not change what the app shows."
            : null;
}
