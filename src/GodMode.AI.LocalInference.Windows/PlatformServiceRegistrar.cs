using Microsoft.Extensions.DependencyInjection;

namespace GodMode.AI.LocalInference.Windows;

public sealed class PlatformServiceRegistrar : IPlatformServiceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<IChatClientFactory, Phi4ChatClientFactory>("directml");
        services.AddSingleton<IHardwareDetector, WinHardwareDetector>();
    }
}
