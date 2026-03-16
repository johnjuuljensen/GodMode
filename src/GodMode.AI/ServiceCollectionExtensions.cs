using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GodMode.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGodModeAIServices(this IServiceCollection services)
    {
        // Register Anthropic as a keyed IChatClientFactory (cross-platform remote inference)
        services.AddKeyedSingleton<IChatClientFactory, AnthropicChatClientFactory>("anthropic");

        // InferenceRouter — central tier-based model routing
        services.AddSingleton<InferenceRouter>();

        // AI defaults — platform projects override via IPlatformServiceRegistrar
        services.TryAddSingleton<IHardwareDetector, NullHardwareDetector>();

        return services;
    }
}
