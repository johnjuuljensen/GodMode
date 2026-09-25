using System.Collections.Concurrent;
using GodMode.Server.Tests.Lifecycle;
using GodMode.Shared;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace GodMode.Server.Tests;

/// <summary>A hub connection, with the server's key, that keeps every StatusChanged it is pushed.</summary>
internal sealed class ServerHubClient(string baseUrl, string apiKey = ServerProcess.ApiKey) : IAsyncDisposable
{
    private readonly ConcurrentQueue<ProjectStatus> _pushes = new();

    public HubConnection Hub { get; } = new HubConnectionBuilder()
        .WithUrl($"{baseUrl}/hubs/projects", options => options.AccessTokenProvider = () => Task.FromResult<string?>(apiKey))
        .AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.PropertyNamingPolicy = JsonDefaults.Options.PropertyNamingPolicy;
            foreach (var converter in JsonDefaults.Options.Converters)
                options.PayloadSerializerOptions.Converters.Add(converter);
        })
        .Build();

    public async Task StartAsync()
    {
        Hub.On<string, ProjectStatus>(nameof(IProjectHubClient.StatusChanged), (_, status) => _pushes.Enqueue(status));
        await Hub.StartAsync();
    }

    /// <summary>The first status pushed for the project, or read with GetStatus, that satisfies <paramref name="condition"/>.</summary>
    public async Task<ProjectStatus> WaitForAsync(string projectId, Func<ProjectStatus, bool> condition, ServerProcess server)
    {
        ProjectStatus? found = null;
        var ok = await LifecycleHarness.WaitForAsync(async () =>
        {
            found = _pushes.FirstOrDefault(s => s.Id == projectId && condition(s));
            if (found != null) return true;
            var current = await Hub.InvokeAsync<ProjectStatus>(nameof(IProjectHub.GetStatus), projectId);
            found = condition(current) ? current : null;
            return found != null;
        });
        Assert.True(ok, $"project {projectId} never reached the expected status.\n{server.Output}");
        return found!;
    }

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}
