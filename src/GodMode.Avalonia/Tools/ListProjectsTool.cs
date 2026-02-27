using System.Text.Json;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class ListProjectsTool(
    IProfileService profileService,
    IHostConnectionService hostService,
    IProjectService projectService) : ITool
{
    public string Name => "list_projects";
    public string Description => "Lists all projects on a server, showing their name, state, and any pending questions.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "server_name", Type = "string", Description = "Server to list projects on (uses first available if omitted)", Required = false },
        new() { Name = "profile_name", Type = "string", Description = "Profile to use (uses current if omitted)", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var (profileName, host, error) = await ToolHelper.ResolveProfileAndHostAsync(
            profileService, hostService, args);
        if (error is not null)
            return ToolResult.Fail(Name, error);

        var projects = (await projectService.ListProjectsAsync(profileName, host.Id)).ToList();
        if (projects.Count == 0)
            return ToolResult.Ok(Name, $"No projects on server '{host.Name}'.");

        var result = projects.Select(p => new
        {
            p.Name,
            p.Id,
            State = p.State.ToString(),
            p.UpdatedAt,
            WaitingForInput = p.CurrentQuestion is not null,
            Question = p.CurrentQuestion
        });

        return ToolResult.Ok(Name, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
