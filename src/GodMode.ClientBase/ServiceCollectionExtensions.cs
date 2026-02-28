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
		var appDataDir = GodModePaths.AppDataDirectory;
		var encryption = new EncryptionHelper(appDataDir);

		services.AddSingleton(encryption);
		services.AddSingleton<IProfileService>(_ => new ProfileService(appDataDir, encryption));
		services.AddSingleton<IHostConnectionService, HostConnectionService>();
		services.AddSingleton<IProjectService, ProjectService>();
		services.AddSingleton<INotificationService, NotificationService>();
		services.AddSingleton<ICredentialService>(_ => new CredentialService(appDataDir, encryption));
		services.AddSingleton<IProjectServerMappingService>(_ => new ProjectServerMappingService(appDataDir));
		return services;
	}
}
