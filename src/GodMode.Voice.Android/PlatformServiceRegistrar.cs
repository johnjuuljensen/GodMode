using GodMode.AI;
using GodMode.Voice.Speech;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Voice.Android;

public sealed class PlatformServiceRegistrar : IPlatformServiceRegistrar
{
	public void RegisterServices(IServiceCollection services)
	{
		services.AddSingleton<ISpeechRecognizer, AndroidSpeechRecognizer>();
		services.AddSingleton<ISpeechSynthesizer, AndroidSpeechSynthesizer>();
	}
}
