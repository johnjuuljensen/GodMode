namespace GodMode.Server.Services;

/// <summary>
/// Runs scripts as part of project creation workflow.
/// </summary>
public interface IScriptRunner
{
    /// <summary>
    /// Runs an array of script commands sequentially.
    /// Scripts are resolved relative to the root directory.
    /// Streams stdout line-by-line via the onProgress callback.
    /// Throws on non-zero exit code.
    /// </summary>
    /// <param name="scripts">Array of script paths relative to the root directory.</param>
    /// <param name="rootPath">The project root directory (scripts are resolved relative to this).</param>
    /// <param name="workingDirectory">Working directory for script execution.</param>
    /// <param name="environment">Environment variables to set for the scripts.</param>
    /// <param name="onProgress">Callback for streaming stdout lines.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunAsync(
        string[] scripts,
        string rootPath,
        string workingDirectory,
        Dictionary<string, string> environment,
        Func<string, Task> onProgress,
        CancellationToken cancellationToken = default);
}
