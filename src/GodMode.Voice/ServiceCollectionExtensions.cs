using GodMode.AI;
using GodMode.AI.Tools;
using GodMode.Voice.AI;
using GodMode.Voice.Services;
using GodMode.Voice.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GodMode.Voice;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGodModeVoiceServices(this IServiceCollection services)
    {
        var config = AIConfig.Load();

        // Register NPU model as keyed service when configured
        if (!string.IsNullOrEmpty(config.NpuModelPath))
            services.AddKeyedSingleton<ILanguageModel, NpuOnnxModel>("npu");

        // InferenceRouter — central tier-based model routing
        services.AddSingleton<InferenceRouter>();
        services.AddSingleton<AssistantService>();

        // Keyed "none" provider for tiers with no model
        services.AddKeyedSingleton<ILanguageModel, NullLanguageModel>("none");

        // AI defaults — platform projects override via IPlatformServiceRegistrar
        services.TryAddSingleton<ILanguageModel, NullLanguageModel>();
        services.TryAddSingleton<IHardwareDetector, NullHardwareDetector>();
        services.TryAddSingleton<ToolRegistry>();

        // Speech: defaults to Whisper + NullSynthesizer; override with platform-specific
        services.TryAddSingleton<ISpeechRecognizer, WhisperSpeechRecognizer>();
        services.TryAddSingleton<ISpeechSynthesizer, NullSpeechSynthesizer>();

        return services;
    }
}
