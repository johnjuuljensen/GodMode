using Microsoft.Extensions.DependencyInjection;

namespace GodMode.AI;

/// <summary>
/// Implemented by platform-specific assemblies to register their services.
/// Discovered at startup via assembly scanning.
/// </summary>
public interface IPlatformServiceRegistrar
{
    void RegisterServices(IServiceCollection services);
}
