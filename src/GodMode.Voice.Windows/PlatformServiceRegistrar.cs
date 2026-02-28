using GodMode.AI;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Voice.Windows;

public sealed class PlatformServiceRegistrar : IPlatformServiceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddGodModeWindowsSpeech();
    }
}
