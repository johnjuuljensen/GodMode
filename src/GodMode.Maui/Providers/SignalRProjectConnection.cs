using GodMode.Maui.Abstractions;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using Microsoft.AspNetCore.SignalR.Client;
using System.Reactive.Subjects;
using System.Reactive.Linq;

namespace GodMode.Maui.Providers;

/// <summary>
/// Implementation of IProjectConnection that uses SignalR to connect to a remote server
/// </summary>
public class SignalRProjectConnection : IProjectConnection
{
    private readonly HubConnection _hubConnection;
    private readonly Dictionary<string, Subject<OutputEvent>> _outputSubscriptions = new();
    private bool _disposed;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public SignalRProjectConnection(string serverUrl)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(serverUrl)
            .WithAutomaticReconnect()
            .Build();

        // Register server-to-client handlers
        _hubConnection.On<string, OutputEvent>("OutputReceived", OnOutputReceived);
        _hubConnection.On<string, ProjectStatus>("StatusChanged", OnStatusChanged);
    }

    public async Task ConnectAsync()
    {
        if (_hubConnection.State == HubConnectionState.Disconnected)
        {
            await _hubConnection.StartAsync();
        }
    }

    public async Task<IEnumerable<ProjectSummary>> ListProjectsAsync()
    {
        return await _hubConnection.InvokeAsync<IEnumerable<ProjectSummary>>("ListProjects");
    }

    public async Task<ProjectStatus> GetStatusAsync(string projectId)
    {
        return await _hubConnection.InvokeAsync<ProjectStatus>("GetStatus", projectId);
    }

    public async Task<ProjectDetail> CreateProjectAsync(string name, string? repoUrl, string initialPrompt)
    {
        var request = new CreateProjectRequest(name, repoUrl, initialPrompt);
        return await _hubConnection.InvokeAsync<ProjectDetail>("CreateProject", request);
    }

    public async Task SendInputAsync(string projectId, string input)
    {
        await _hubConnection.InvokeAsync("SendInput", projectId, input);
    }

    public async Task StopProjectAsync(string projectId)
    {
        await _hubConnection.InvokeAsync("StopProject", projectId);
    }

    public IObservable<OutputEvent> SubscribeOutput(string projectId, long fromOffset = 0)
    {
        if (!_outputSubscriptions.ContainsKey(projectId))
        {
            _outputSubscriptions[projectId] = new Subject<OutputEvent>();

            // Subscribe on the server
            _hubConnection.InvokeAsync("SubscribeProject", projectId, fromOffset).ConfigureAwait(false);
        }

        return _outputSubscriptions[projectId].AsObservable();
    }

    public async Task<string> GetMetricsHtmlAsync(string projectId)
    {
        return await _hubConnection.InvokeAsync<string>("GetMetricsHtml", projectId);
    }

    public void Disconnect()
    {
        _hubConnection.StopAsync().ConfigureAwait(false);
    }

    private void OnOutputReceived(string projectId, OutputEvent outputEvent)
    {
        if (_outputSubscriptions.TryGetValue(projectId, out var subject))
        {
            subject.OnNext(outputEvent);
        }
    }

    private void OnStatusChanged(string projectId, ProjectStatus status)
    {
        // This could be used to update local cache or trigger UI updates
        // For now, we'll leave it as a placeholder
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var subject in _outputSubscriptions.Values)
            {
                subject.Dispose();
            }
            _outputSubscriptions.Clear();

            _hubConnection.DisposeAsync().AsTask().Wait();
            _disposed = true;
        }
    }
}
