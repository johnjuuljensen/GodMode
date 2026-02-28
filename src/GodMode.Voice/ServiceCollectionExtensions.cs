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
        var provider = config.ExecutionProvider?.ToLowerInvariant() ?? "auto";

        // Register NPU language model when configured — overrides platform registrar default
        if (provider == "npu" || (provider == "auto" && !string.IsNullOrEmpty(config.NpuModelPath)))
            services.AddSingleton<ILanguageModel, NpuOnnxModel>();

        services.AddSingleton<AssistantService>();

        // AI defaults — platform projects override via IPlatformServiceRegistrar
        services.TryAddSingleton<ILanguageModel, NullLanguageModel>();
        services.TryAddSingleton<ToolRegistry>();

        // Speech: defaults to Whisper + NullSynthesizer; override with platform-specific
        services.TryAddSingleton<ISpeechRecognizer, WhisperSpeechRecognizer>();
        services.TryAddSingleton<ISpeechSynthesizer, NullSpeechSynthesizer>();

        return services;
    }
}
