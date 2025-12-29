using GodMode.Shared.Models;
using GodMode.Shared.Enums;

namespace GodMode.Server.Models;

/// <summary>
/// Internal project information tracked by the server.
/// </summary>
public class ProjectInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>
    /// The project directory path. This is also the working directory for Claude.
    /// </summary>
    public required string ProjectPath { get; init; }
    public string? RepoUrl { get; set; }
    public string? SessionId { get; set; }
    
    public ProjectState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? CurrentQuestion { get; set; }
    
    public ProjectMetrics Metrics { get; set; } = new(0, 0, 0, TimeSpan.Zero, 0);
    public GitStatus? Git { get; set; }
    public TestStatus? Tests { get; set; }
    
    public long OutputOffset { get; set; }
    
    public int ProcessId { get; set; }
    public CancellationTokenSource? ProcessCancellation { get; set; }
    
    public HashSet<string> SubscribedConnections { get; } = new();
}
