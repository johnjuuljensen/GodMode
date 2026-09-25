using GodMode.Server.Models;

namespace GodMode.Server.Services;

/// <summary>
/// Interface for managing Claude Code processes. A process's stdout goes to its project's
/// <see cref="ProjectProcess.Output"/> pipeline, and its exit follows as the last item
/// (<see cref="PipelineItem.Exited"/>). The manager keeps <see cref="ProjectProcess.ProcessId"/>.
/// </summary>
public interface IClaudeProcessManager
{
    /// <summary>
    /// Starts a new Claude process with an initial prompt and new session ID.
    /// </summary>
    /// <param name="project">The project info.</param>
    /// <param name="initialPrompt">The initial prompt to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="extraEnvironment">Additional environment variables to set on the process.</param>
    /// <param name="extraArgs">Additional CLI arguments to append.</param>
    Task<int> StartClaudeProcessAsync(
        ProjectInfo project,
        string initialPrompt,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment = null,
        string[]? extraArgs = null);

    /// <summary>
    /// Resumes a Claude process using --resume flag with existing session ID.
    /// </summary>
    /// <param name="project">The project info.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="extraEnvironment">Additional environment variables to set on the process.</param>
    /// <param name="extraArgs">Additional CLI arguments to append.</param>
    Task<int> ResumeClaudeProcessAsync(
        ProjectInfo project,
        CancellationToken cancellationToken,
        Dictionary<string, string>? extraEnvironment = null,
        string[]? extraArgs = null);

    Task SendInputAsync(ProjectInfo project, string input);

    /// <summary>
    /// Stops the process, gracefully first: interrupts it, waits up to <paramref name="grace"/> (the
    /// configured grace period by default) for it to exit, then kills its whole process tree. Returns
    /// once its exit is on the pipeline, after all its output.
    /// </summary>
    Task StopProcessAsync(ProjectInfo project, TimeSpan? grace = null);

    /// <summary>
    /// Waits while the project's process has exited but its exit is not handled yet: its output still
    /// draining, or a fresh session taking its place. After it, <see cref="IsProcessRunning"/> of the
    /// project's process says whether it has one.
    /// </summary>
    Task SettleAsync(ProjectInfo project);

    /// <summary>How long a stop gives claude to exit once interrupted.</summary>
    TimeSpan StopGracePeriod { get; }

    bool IsProcessRunning(int processId);
}
