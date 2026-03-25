using GodMode.ClientBase.Logging;
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
        // Logging: file sink (host can add more sinks like AddDebug)
        var logDir = Path.Combine(GodModePaths.AppDataDirectory, "logs");
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new FileLoggerProvider(logDir));
        });

        services.AddSingleton<ITokenProtector, TokenProtector>();
        services.AddSingleton<IServerRegistryService>(sp =>
            new ServerRegistryService(GodModePaths.AppDataDirectory, sp.GetRequiredService<ITokenProtector>()));
        services.AddSingleton<IServerConnectionService, ServerConnectionService>();
        return services;
    }
}
