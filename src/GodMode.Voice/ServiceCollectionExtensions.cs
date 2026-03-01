using GodMode.AI;
using GodMode.AI.Tools;
using GodMode.Voice.Services;
using GodMode.Voice.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GodMode.Voice;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGodModeVoiceServices(this IServiceCollection services)
    {
        // Register Anthropic as a keyed IChatClientFactory (cross-platform remote inference)
        services.AddKeyedSingleton<IChatClientFactory, AnthropicChatClientFactory>("anthropic");

        // InferenceRouter — central tier-based model routing
        services.AddSingleton<InferenceRouter>();
        services.AddSingleton<AssistantService>();

        // AI defaults — platform projects override via IPlatformServiceRegistrar
        services.TryAddSingleton<IHardwareDetector, NullHardwareDetector>();
        services.TryAddSingleton<ToolRegistry>();

        // Speech: defaults to Whisper + NullSynthesizer; override with platform-specific
        services.TryAddSingleton<ISpeechRecognizer, WhisperSpeechRecognizer>();
        services.TryAddSingleton<ISpeechSynthesizer, NullSpeechSynthesizer>();

        return services;
    }
}
