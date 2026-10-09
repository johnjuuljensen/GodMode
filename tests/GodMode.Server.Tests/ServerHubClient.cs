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
        .WithServerTimeout(TestTimeouts.Request)
        .Build();

    public async Task StartAsync()
    {
        Hub.On<string, ProjectStatus>(nameof(IProjectHubClient.StatusChanged), (_, status) => _pushes.Enqueue(status));
        Hub.HandshakeTimeout = TestTimeouts.Request;
        // A server only just started can take longer than its own 15 s to read the handshake on a loaded
        // machine, and refuses it as canceled: the connection is not what the tests are about (#371)
        var deadline = DateTime.UtcNow + TestTimeouts.Request;
        while (true)
        {
            try
            {
                await Hub.StartAsync();
                return;
            }
            catch (Microsoft.AspNetCore.SignalR.HubException ex) when (ex.Message.EndsWith("Handshake was canceled.", StringComparison.Ordinal) && DateTime.UtcNow < deadline) { }
        }
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
