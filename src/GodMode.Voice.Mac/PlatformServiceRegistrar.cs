using GodMode.AI;
using GodMode.Voice.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Voice.Mac;

public sealed class PlatformServiceRegistrar : IPlatformServiceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ISpeechRecognizer, MacSpeechRecognizer>();
        services.AddSingleton<ISpeechSynthesizer, MacSpeechSynthesizer>();
    }
}
