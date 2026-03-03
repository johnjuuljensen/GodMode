using GodMode.ClientBase.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.ClientBase;

/// <summary>
/// Registers all GodMode client services. Shared across all UI frontends.
/// </summary>
public static class ServiceCollectionExtensions
{
	public static IServiceCollection AddGodModeClientServices(this IServiceCollection services)
	{
		services.AddSingleton<ITokenProtector, TokenProtector>();
		services.AddSingleton<IServerRegistryService>(sp =>
			new ServerRegistryService(GodModePaths.AppDataDirectory, sp.GetRequiredService<ITokenProtector>()));
		services.AddSingleton<IHostConnectionService, HostConnectionService>();
		services.AddSingleton<IProjectService, ProjectService>();
		services.AddSingleton<INotificationService, NotificationService>();
		return services;
	}
}
