using GodMode.Maui.Abstractions;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;

namespace GodMode.Maui.Providers;

/// <summary>
/// Host provider for local folder-based projects
/// </summary>
public class LocalFolderProvider : IHostProvider
{
    private readonly string _rootPath;
    private readonly string _hostId;
    private readonly string _hostName;

    public string Type => "local";

    public LocalFolderProvider(string rootPath, string? hostName = null)
    {
        _rootPath = rootPath;
        _hostId = Path.GetFileName(rootPath) ?? "local";
        _hostName = hostName ?? _hostId;

        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }
    }

    public Task<IEnumerable<HostInfo>> ListHostsAsync()
    {
        var hosts = new List<HostInfo>
        {
            new HostInfo(
                _hostId,
                _hostName,
                "local",
                Directory.Exists(_rootPath) ? HostState.Running : HostState.Stopped,
                _rootPath
            )
        };

        return Task.FromResult<IEnumerable<HostInfo>>(hosts);
    }

    public Task<HostStatus> GetHostStatusAsync(string hostId)
    {
        if (hostId != _hostId)
        {
            throw new ArgumentException($"Unknown host: {hostId}");
        }

        var projectsPath = _rootPath;
        var activeProjects = 0;

        if (Directory.Exists(projectsPath))
        {
            activeProjects = Directory.GetDirectories(projectsPath)
                .Count(dir => File.Exists(Path.Combine(dir, "status.json")));
        }

        var status = new HostStatus(
            _hostId,
            _hostName,
            "local",
            Directory.Exists(_rootPath) ? HostState.Running : HostState.Stopped,
            _rootPath,
            activeProjects,
            DateTime.UtcNow
        );

        return Task.FromResult(status);
    }

    public Task StartHostAsync(string hostId)
    {
        // Local hosts are always "running" - nothing to start
        return Task.CompletedTask;
    }

    public Task StopHostAsync(string hostId)
    {
        // Local hosts can't be stopped from the UI
        return Task.CompletedTask;
    }

    public Task<IProjectConnection> ConnectAsync(string hostId)
    {
        if (hostId != _hostId)
        {
            throw new ArgumentException($"Unknown host: {hostId}");
        }

        // Try to connect via SignalR first (if a local server is running)
        // Fall back to direct file access if not available
        var connection = TryConnectSignalR() ?? new LocalProjectConnection(_rootPath);

        return Task.FromResult(connection);
    }

    private IProjectConnection? TryConnectSignalR()
    {
        try
        {
            // Try to connect to local SignalR server on default port
            var serverUrl = "http://localhost:5000/projecthub";
            var connection = new SignalRProjectConnection(serverUrl);

            // Try to connect with a timeout
            var connectTask = connection.ConnectAsync();
            if (connectTask.Wait(TimeSpan.FromSeconds(2)))
            {
                return connection;
            }
        }
        catch
        {
            // SignalR not available, fall back to local file access
        }

        return null;
    }
}
