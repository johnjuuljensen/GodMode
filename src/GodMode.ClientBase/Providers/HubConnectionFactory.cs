using GodMode.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Creates and starts HubConnections to GodMode.Server instances
/// with standard configuration (JSON protocol, auto-reconnect, optional auth).
/// </summary>
public static class HubConnectionFactory
{
    public static async Task<HubConnection> CreateAndStartAsync(string serverUrl, string? accessToken = null)
    {
        var hubUrl = serverUrl.TrimEnd('/') + "/hubs/projects";

        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrEmpty(accessToken))
                    options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                var defaults = JsonDefaults.Options;
                options.PayloadSerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = defaults.DefaultIgnoreCondition;
                foreach (var converter in defaults.Converters)
                    options.PayloadSerializerOptions.Converters.Add(converter);
            });

        var connection = builder.Build();
        await connection.StartAsync();
        return connection;
    }
}
