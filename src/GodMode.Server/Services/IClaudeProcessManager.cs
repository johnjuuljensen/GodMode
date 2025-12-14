using GodMode.Server.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Interface for managing Claude Code processes.
/// </summary>
public interface IClaudeProcessManager
{
    Task<int> StartClaudeProcessAsync(ProjectInfo project, string initialPrompt, CancellationToken cancellationToken);
    Task<int> ResumeClaudeProcessAsync(ProjectInfo project, CancellationToken cancellationToken);
    Task SendInputAsync(ProjectInfo project, string input);
    Task StopProcessAsync(ProjectInfo project);
    bool IsProcessRunning(int processId);
}
