using GodMode.ClientBase.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GodMode.ClientBase;

/// <summary>
/// Registers GodMode client services.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGodModeClientServices(this IServiceCollection services)
    {
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton<ITokenProtector, TokenProtector>();
        services.AddSingleton<IServerRegistryService>(sp =>
            new ServerRegistryService(GodModePaths.AppDataDirectory, sp.GetRequiredService<ITokenProtector>()));
        services.AddSingleton<IServerConnectionService, ServerConnectionService>();
        return services;
    }
}
