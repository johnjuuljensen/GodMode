using GodMode.Server.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Callback invoked when Claude process produces output.
/// </summary>
public delegate Task OutputReceivedHandler(ProjectInfo project, string jsonLine);

/// <summary>
/// Interface for managing Claude Code processes.
/// </summary>
public interface IClaudeProcessManager
{
    /// <summary>
    /// Event raised when a Claude process produces output.
    /// </summary>
    event OutputReceivedHandler? OnOutputReceived;

    /// <summary>
    /// Starts a new Claude process with an initial prompt and new session ID.
    /// </summary>
    Task<int> StartClaudeProcessAsync(ProjectInfo project, string initialPrompt, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a Claude process using --resume flag with existing session ID.
    /// </summary>
    Task<int> ResumeClaudeProcessAsync(ProjectInfo project, CancellationToken cancellationToken);

    Task SendInputAsync(ProjectInfo project, string input);
    Task StopProcessAsync(ProjectInfo project);
    bool IsProcessRunning(int processId);
}
