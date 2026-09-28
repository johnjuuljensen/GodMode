using GodMode.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SignalR.Proxy;

namespace GodMode.ClientBase.Hub;

/// <summary>Hub connections from .NET clients straight to a server (not through the WebView's relay).</summary>
public static class HubConnections
{
    /// <summary>
    /// A connection to <paramref name="target"/>'s hub, presenting its key, with the server's payload conventions
    /// (PascalCase, string enums). Not started.
    /// </summary>
    public static HubConnection Build(RelayTarget target) =>
        new HubConnectionBuilder()
            .WithUrl(target.HubUrl, options =>
            {
                if (target.AccessToken != null)
                    options.AccessTokenProvider = () => Task.FromResult<string?>(target.AccessToken);
            })
            .AddJsonProtocol(options =>
            {
                var defaults = JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            })
            .Build();
}
