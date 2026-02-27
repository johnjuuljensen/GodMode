using GodMode.Voice.AI;
using GodMode.Voice.Services;
using GodMode.Voice.Speech;
using GodMode.Voice.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GodMode.Voice;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGodModeVoiceServices(this IServiceCollection services)
    {
        services.AddSingleton<ILanguageModel, Phi4MiniOnnxModel>();
        services.AddSingleton<AssistantService>();

        // Speech: defaults to Whisper + NullSynthesizer; override with platform-specific
        services.TryAddSingleton<ISpeechRecognizer, WhisperSpeechRecognizer>();
        services.TryAddSingleton<ISpeechSynthesizer, NullSpeechSynthesizer>();

        return services;
    }
}
