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
        [.. new[] { (Name: "ElevenLabs", Key: keys.ElevenLabs), (Name: "Claude", Key: keys.Anthropic) }
            .Where(k => string.IsNullOrEmpty(k.Key)).Select(k => k.Name)];

    public void Register(IServiceCollection services, VoiceSettings settings, ElevenLabsLanguageOptions language)
    {
        if (MissingKeys.Count > 0)
            throw new InvalidOperationException($"No {string.Join(" or ", MissingKeys)} key is set");

        services.AddVoiceBotAI();
        services.AddVoiceBotAnthropic(keys.Anthropic!);
        services.AddVoiceBotElevenLabs(keys.ElevenLabs!, settings.VoiceId, TtsModel, SttModel, Speed, language: language);
    }

    public Task InitializeAsync(IServiceProvider services, VoiceSettings settings) =>
        services.GetRequiredService<InferenceRouter>().InitializeAsync(TierMap(settings.TierModels));

    /// <summary>
    /// The model behind each tier: the one the user set (<see cref="VoiceSettings.TierModels"/>, #525), else VoiceBot's,
    /// so a VoiceBot pin that moves its models moves every tier the user left empty (#475). The same merge as VoiceBot's
    /// <c>ReadTierMap</c> does from configuration.
    /// </summary>
    public static Dictionary<InferenceTier, TierConfig> TierMap(VoiceTierModels? overrides = null) =>
        TierMapConfiguration.DefaultModels.ToDictionary(
            d => d.Key,
            d => new TierConfig(TierMapConfiguration.DefaultProvider, overrides?[d.Key] ?? d.Value));
}
