using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

public interface IStatusUpdater
{
    Task SaveStatusAsync(ProjectInfo project);
    Task<bool> UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent);
    Task UpdateGitStatusAsync(ProjectInfo project);
}
