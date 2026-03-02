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
		services.AddSingleton<IServerRegistryService>(_ => new ServerRegistryService(GodModePaths.AppDataDirectory));
		services.AddSingleton<IHostConnectionService, HostConnectionService>();
		services.AddSingleton<IProjectService, ProjectService>();
		services.AddSingleton<INotificationService, NotificationService>();
		return services;
	}
}
