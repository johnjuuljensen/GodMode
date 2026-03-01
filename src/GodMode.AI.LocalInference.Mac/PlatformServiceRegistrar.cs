using Microsoft.Extensions.DependencyInjection;

namespace GodMode.AI.LocalInference.Mac;

public sealed class PlatformServiceRegistrar : IPlatformServiceRegistrar
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<ILanguageModel, OnnxLanguageModel>();
        services.AddKeyedSingleton<ILanguageModel, OnnxLanguageModel>("cpu");
    }
}
