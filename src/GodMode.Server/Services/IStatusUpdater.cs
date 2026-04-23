using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

public interface IStatusUpdater
{
    Task SaveStatusAsync(ProjectInfo project);
    Task UpdateFromOutputEventAsync(ProjectInfo project, OutputEvent outputEvent, string rawJson);
    Task UpdateGitStatusAsync(ProjectInfo project);
}
