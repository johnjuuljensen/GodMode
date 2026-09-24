using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

public interface IStatusUpdater
{
    Task SaveStatusAsync(ProjectInfo project);
    /// <summary>Applies an output event to the project's status and saves it; true if the status changed.</summary>
    Task<bool> UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent, string rawJson);
    Task UpdateGitStatusAsync(ProjectInfo project);
}
