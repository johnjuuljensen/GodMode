using GodMode.Maui.Abstractions;
using GodMode.Shared.Models;
using GodMode.Shared.Enums;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Text.Json;

namespace GodMode.Maui.Providers;

/// <summary>
/// Implementation of IProjectConnection that directly accesses local file system
/// </summary>
public class LocalProjectConnection : IProjectConnection
{
    private readonly string _projectsRootPath;
    private readonly Dictionary<string, Subject<OutputEvent>> _outputSubscriptions = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, long> _outputOffsets = new();
    private bool _disposed;

    public bool IsConnected => true; // Always connected for local access

    public LocalProjectConnection(string projectsRootPath)
    {
        _projectsRootPath = projectsRootPath;

        if (!Directory.Exists(_projectsRootPath))
        {
            Directory.CreateDirectory(_projectsRootPath);
        }
    }

    public Task<IEnumerable<ProjectSummary>> ListProjectsAsync()
    {
        var summaries = new List<ProjectSummary>();

        if (!Directory.Exists(_projectsRootPath))
        {
            return Task.FromResult<IEnumerable<ProjectSummary>>(summaries);
        }

        foreach (var projectDir in Directory.GetDirectories(_projectsRootPath))
        {
            var statusPath = Path.Combine(projectDir, "status.json");
            if (File.Exists(statusPath))
            {
                try
                {
                    var statusJson = File.ReadAllText(statusPath);
                    var status = JsonSerializer.Deserialize<ProjectStatus>(statusJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (status != null)
                    {
                        summaries.Add(new ProjectSummary(
                            status.Id,
                            status.Name,
                            status.State,
                            status.UpdatedAt,
                            status.CurrentQuestion
                        ));
                    }
                }
                catch
                {
                    // Skip invalid project directories
                }
            }
        }

        return Task.FromResult<IEnumerable<ProjectSummary>>(summaries);
    }

    public Task<ProjectStatus> GetStatusAsync(string projectId)
    {
        var statusPath = Path.Combine(_projectsRootPath, projectId, "status.json");

        if (!File.Exists(statusPath))
        {
            throw new FileNotFoundException($"Project {projectId} not found");
        }

        var statusJson = File.ReadAllText(statusPath);
        var status = JsonSerializer.Deserialize<ProjectStatus>(statusJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return Task.FromResult(status ?? throw new InvalidOperationException("Failed to deserialize project status"));
    }

    public Task<ProjectDetail> CreateProjectAsync(string name, string? repoUrl, string initialPrompt)
    {
        var projectId = GenerateProjectId(name);
        var projectPath = Path.Combine(_projectsRootPath, projectId);
        var workPath = Path.Combine(projectPath, "work");

        Directory.CreateDirectory(projectPath);
        Directory.CreateDirectory(workPath);

        var status = new ProjectStatus(
            projectId,
            name,
            ProjectState.Idle,
            DateTime.UtcNow,
            DateTime.UtcNow,
            repoUrl,
            null,
            new ProjectMetrics(0, 0, 0, TimeSpan.Zero, 0),
            null,
            null,
            0
        );

        var statusPath = Path.Combine(projectPath, "status.json");
        File.WriteAllText(statusPath, JsonSerializer.Serialize(status, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        // Create empty input/output logs
        File.Create(Path.Combine(projectPath, "input.jsonl")).Close();
        File.Create(Path.Combine(projectPath, "output.jsonl")).Close();

        // Generate a session ID for this project
        var sessionId = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(projectPath, "session-id"), sessionId);

        var detail = new ProjectDetail(status, sessionId);

        return Task.FromResult(detail);
    }

    public Task SendInputAsync(string projectId, string input)
    {
        var inputPath = Path.Combine(_projectsRootPath, projectId, "input.jsonl");

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Project {projectId} not found");
        }

        var inputEvent = new OutputEvent(
            DateTime.UtcNow,
            OutputEventType.UserInput,
            input
        );

        var json = JsonSerializer.Serialize(inputEvent);
        File.AppendAllText(inputPath, json + Environment.NewLine);

        return Task.CompletedTask;
    }

    public Task StopProjectAsync(string projectId)
    {
        // In a local implementation, this would need to find and terminate the Claude process
        // For now, we'll just update the status
        var statusPath = Path.Combine(_projectsRootPath, projectId, "status.json");

        if (File.Exists(statusPath))
        {
            var statusJson = File.ReadAllText(statusPath);
            var status = JsonSerializer.Deserialize<ProjectStatus>(statusJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (status != null)
            {
                var updatedStatus = status with { State = ProjectState.Stopped, UpdatedAt = DateTime.UtcNow };
                File.WriteAllText(statusPath, JsonSerializer.Serialize(updatedStatus, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            }
        }

        return Task.CompletedTask;
    }

    public IObservable<OutputEvent> SubscribeOutput(string projectId, long fromOffset = 0)
    {
        if (!_outputSubscriptions.ContainsKey(projectId))
        {
            _outputSubscriptions[projectId] = new Subject<OutputEvent>();
            _outputOffsets[projectId] = fromOffset;

            // Read existing output from offset
            ReadOutputFromOffset(projectId, fromOffset);

            // Set up file watcher for new output
            SetupFileWatcher(projectId);
        }

        return _outputSubscriptions[projectId].AsObservable();
    }

    public Task<string> GetMetricsHtmlAsync(string projectId)
    {
        var metricsPath = Path.Combine(_projectsRootPath, projectId, "metrics.html");

        if (File.Exists(metricsPath))
        {
            return Task.FromResult(File.ReadAllText(metricsPath));
        }

        return Task.FromResult("<html><body><h1>No metrics available</h1></body></html>");
    }

    public void Disconnect()
    {
        // Nothing to disconnect for local access
    }

    private void ReadOutputFromOffset(string projectId, long offset)
    {
        var outputPath = Path.Combine(_projectsRootPath, projectId, "output.jsonl");

        if (!File.Exists(outputPath))
        {
            return;
        }

        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            try
            {
                var outputEvent = JsonSerializer.Deserialize<OutputEvent>(line, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (outputEvent != null && _outputSubscriptions.TryGetValue(projectId, out var subject))
                {
                    subject.OnNext(outputEvent);
                }
            }
            catch
            {
                // Skip malformed lines
            }
        }

        _outputOffsets[projectId] = stream.Position;
    }

    private void SetupFileWatcher(string projectId)
    {
        var projectPath = Path.Combine(_projectsRootPath, projectId);
        var watcher = new FileSystemWatcher(projectPath, "output.jsonl")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        watcher.Changed += (sender, e) => ReadOutputFromOffset(projectId, _outputOffsets.GetValueOrDefault(projectId, 0));
        watcher.EnableRaisingEvents = true;

        _watchers[projectId] = watcher;
    }

    private static string GenerateProjectId(string name)
    {
        var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return $"{sanitized.ToLower()}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }
            _watchers.Clear();

            foreach (var subject in _outputSubscriptions.Values)
            {
                subject.Dispose();
            }
            _outputSubscriptions.Clear();

            _disposed = true;
        }
    }
}
