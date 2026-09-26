using Microsoft.Extensions.DependencyInjection;
using VoiceBot.AI;
using VoiceBot.Core.AI;
using VoiceBot.Providers.Anthropic;
using VoiceBot.Providers.ElevenLabs;

namespace GodMode.Voice;

/// <summary>
/// The speech and model services behind a voice session: VoiceBot's <c>ISpeechEngine</c> (for audio input),
/// <c>ISpeechSynthesizer</c>, <c>ITranscriptionCleaner</c> and <see cref="IInferenceProvider"/>.
/// </summary>
public interface IVoiceProviders
{
    void Register(IServiceCollection services, VoiceSettings settings, ElevenLabsLanguageOptions language);

    /// <summary>After the services are built, before the session starts.</summary>
    Task InitializeAsync(IServiceProvider services, VoiceSettings settings);
}

/// <summary>ElevenLabs for speech both ways, Anthropic for the model, with the user's keys.</summary>
public sealed class CloudVoiceProviders(VoiceKeys keys) : IVoiceProviders
{
    public const string TtsModel = "eleven_flash_v2_5";
    public const string SttModel = "scribe_v2_realtime";
    public const float Speed = 1.2f;

    /// <summary>The keys voice cannot start without, by what the settings call them; empty when both are set.</summary>
    public IReadOnlyList<string> MissingKeys =>
        [.. new[] { ("ElevenLabs", keys.ElevenLabs), ("Claude", keys.Anthropic) }.Where(k => string.IsNullOrEmpty(k.Item2)).Select(k => k.Item1)];

    public void Register(IServiceCollection services, VoiceSettings settings, ElevenLabsLanguageOptions language)
    {
        if (MissingKeys.Count > 0)
            throw new InvalidOperationException($"No {string.Join(" or ", MissingKeys)} key is set");

        services.AddVoiceBotAI();
        services.AddVoiceBotAnthropic(keys.Anthropic!, settings.Models.Medium);
        services.AddVoiceBotElevenLabs(keys.ElevenLabs!, settings.VoiceId, TtsModel, SttModel, Speed, language: language);
    }

    public Task InitializeAsync(IServiceProvider services, VoiceSettings settings) =>
        services.GetRequiredService<InferenceRouter>().InitializeAsync(new Dictionary<InferenceTier, TierConfig>
        {
            [InferenceTier.Light] = new("anthropic", settings.Models.Light),
            [InferenceTier.Medium] = new("anthropic", settings.Models.Medium),
            [InferenceTier.Heavy] = new("anthropic", settings.Models.Heavy),
        });
}
