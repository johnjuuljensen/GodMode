using GodMode.ClientBase.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.ClientBase;

/// <summary>
/// Registers GodMode client services.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGodModeClientServices(this IServiceCollection services)
    {
        services.AddSingleton<ITokenProtector, TokenProtector>();
        services.AddSingleton<IServerRegistryService>(sp =>
            new ServerRegistryService(GodModePaths.AppDataDirectory, sp.GetRequiredService<ITokenProtector>()));
        services.AddSingleton<IHostConnectionService, HostConnectionService>();
        return services;
    }
}
