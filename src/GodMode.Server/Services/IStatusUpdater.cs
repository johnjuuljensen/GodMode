using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

public interface IStatusUpdater
{
    Task SaveStatusAsync(ProjectInfo project);
    /// <summary>
    /// Applies an output event to the project's status, in memory; true if the status changed. The
    /// caller saves it, so a save that fails does not stop the change from being pushed.
    /// </summary>
    Task<bool> UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent, string rawJson);
    Task UpdateGitStatusAsync(ProjectInfo project);
}
