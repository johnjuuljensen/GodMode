using GodMode.Voice.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Voice.Windows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGodModeWindowsSpeech(this IServiceCollection services)
    {
        services.AddSingleton<ISpeechRecognizer, WindowsSpeechRecognizer>();
        services.AddSingleton<ISpeechSynthesizer, WindowsSpeechSynthesizer>();
        return services;
    }
}
