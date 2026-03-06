using System.Text.Json;
using GodMode.ClientBase.Abstractions;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using TypedSignalR.Client;

namespace GodMode.ClientBase.Providers;

/// <summary>
/// Implementation of IProjectConnection that uses SignalR to connect to a remote server
/// </summary>
public class SignalRProjectConnection : IProjectConnection, IProjectHubClient
{
    private readonly HubConnection _hubConnection;
    private readonly IProjectHub _hubProxy;
    private readonly IDisposable _clientRegistration;
    private readonly Dictionary<string, Subject<ClaudeMessage>> _outputSubscriptions = new();
    private bool _disposed;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public event Action<string, string>? CreationProgressReceived;
    public event Action<ProjectStatus>? ProjectCreatedReceived;
    public event Action<string>? ProjectDeletedReceived;
    public event Action<string, ProjectStatus>? StatusChangedReceived;

    /// <summary>
    /// Creates a new SignalR connection to a project server.
    /// </summary>
    /// <param name="serverUrl">The SignalR hub URL.</param>
    /// <param name="accessToken">Optional access token for authentication (e.g., GitHub PAT for Codespaces).</param>
    public SignalRProjectConnection(string serverUrl, string? accessToken = null)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl(serverUrl, options =>
            {
                if (!string.IsNullOrEmpty(accessToken))
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                }
            })
            .WithAutomaticReconnect();

        _hubConnection = builder.Build();

        // Create strongly-typed hub proxy for server calls
        _hubProxy = _hubConnection.CreateHubProxy<IProjectHub>();

        // Register strongly-typed client handlers for server-to-client calls
        _clientRegistration = _hubConnection.Register<IProjectHubClient>(this);
    }

    public async Task ConnectAsync()
    {
        if (_hubConnection.State == HubConnectionState.Disconnected)
        {
            await _hubConnection.StartAsync();
        }
    }

    public async Task<IEnumerable<ProfileInfo>> ListProfilesAsync()
    {
        try
        {
            return await _hubProxy.ListProfiles();
        }
        catch
        {
            // Old servers don't support ListProfiles — return empty
            return [];
        }
    }

    public async Task<IEnumerable<ProjectRootInfo>> ListProjectRootsAsync()
    {
        return await _hubProxy.ListProjectRoots();
    }

    public async Task<IEnumerable<ProjectSummary>> ListProjectsAsync()
    {
        return await _hubProxy.ListProjects();
    }

    public async Task<ProjectStatus> GetStatusAsync(string projectId)
    {
        return await _hubProxy.GetStatus(projectId);
    }

    public async Task<ProjectStatus> CreateProjectAsync(string profileName, string projectRootName, string? actionName, Dictionary<string, JsonElement> inputs)
    {
        return await _hubProxy.CreateProject(profileName, projectRootName, actionName, inputs);
    }

    public async Task SendInputAsync(string projectId, string input)
    {
        await _hubProxy.SendInput(projectId, input);
    }

    public async Task StopProjectAsync(string projectId)
    {
        await _hubProxy.StopProject(projectId);
    }

    public async Task ResumeProjectAsync(string projectId)
    {
        await _hubProxy.ResumeProject(projectId);
    }

    public async Task DeleteProjectAsync(string projectId, bool force = false)
    {
        await _hubProxy.DeleteProject(projectId, force);
    }

    public IObservable<ClaudeMessage> SubscribeOutput(string projectId, long fromOffset = 0)
    {
        if (!_outputSubscriptions.ContainsKey(projectId))
        {
            _outputSubscriptions[projectId] = new Subject<ClaudeMessage>();
        }

        // Always re-subscribe on the server to replay history from the requested offset.
        // The Subject is hot — previous events aren't replayed to new observers,
        // so we need the server to re-send from the file.
        _hubProxy.SubscribeProject(projectId, fromOffset).ConfigureAwait(false);

        return _outputSubscriptions[projectId].AsObservable();
    }

    public async Task<string> GetMetricsHtmlAsync(string projectId)
    {
        return await _hubProxy.GetMetricsHtml(projectId);
    }

    public void Disconnect()
    {
        _hubConnection.StopAsync().ConfigureAwait(false);
    }

    #region IProjectHubClient Implementation

    Task IProjectHubClient.OutputReceived(string projectId, string rawJson)
    {
        if (_outputSubscriptions.TryGetValue(projectId, out var subject))
        {
            var message = new ClaudeMessage(rawJson);
            subject.OnNext(message);
        }
        return Task.CompletedTask;
    }

    Task IProjectHubClient.StatusChanged(string projectId, ProjectStatus status)
    {
        StatusChangedReceived?.Invoke(projectId, status);
        return Task.CompletedTask;
    }

    Task IProjectHubClient.ProjectCreated(ProjectStatus status)
    {
        ProjectCreatedReceived?.Invoke(status);
        return Task.CompletedTask;
    }

    Task IProjectHubClient.CreationProgress(string projectId, string message)
    {
        CreationProgressReceived?.Invoke(projectId, message);
        return Task.CompletedTask;
    }

    Task IProjectHubClient.ProjectDeleted(string projectId)
    {
        // Clean up output subscription for deleted project
        if (_outputSubscriptions.TryGetValue(projectId, out var subject))
        {
            subject.OnCompleted();
            subject.Dispose();
            _outputSubscriptions.Remove(projectId);
        }

        ProjectDeletedReceived?.Invoke(projectId);
        return Task.CompletedTask;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _clientRegistration.Dispose();

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
