using System.Text.Json;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class ProjectStatusTool(
    IProfileService profileService,
    IHostConnectionService hostService,
    IProjectService projectService) : ITool
{
    public string Name => "project_status";
    public string Description => "Gets detailed status of a project including state, metrics, git info, and any pending question.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_name", Type = "string", Description = "The name of the project to check", Required = true },
        new() { Name = "server_name", Type = "string", Description = "Server the project is on (uses first available if omitted)", Required = false },
        new() { Name = "profile_name", Type = "string", Description = "Profile to use (uses current if omitted)", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var projectName = ToolHelper.ExtractString(args, "project_name");
        if (string.IsNullOrWhiteSpace(projectName))
            return ToolResult.Fail(Name, "Missing required parameter: project_name");

        var (profileName, host, resolveError) = await ToolHelper.ResolveProfileAndHostAsync(
            profileService, hostService, args);
        if (resolveError is not null)
            return ToolResult.Fail(Name, resolveError);

        var (project, projectError) = await ToolHelper.ResolveProjectAsync(
            projectService, profileName, host.Id, projectName);
        if (project is null)
            return ToolResult.Fail(Name, projectError!);

        var status = await projectService.GetStatusAsync(profileName, host.Id, project.Id);

        var result = new
        {
            status.Name,
            State = status.State.ToString(),
            status.CreatedAt,
            status.UpdatedAt,
            WaitingForInput = status.CurrentQuestion is not null,
            Question = status.CurrentQuestion,
            Metrics = new
            {
                status.Metrics.InputTokens,
                status.Metrics.OutputTokens,
                status.Metrics.ToolCalls,
                Duration = status.Metrics.Duration.ToString(@"hh\:mm\:ss"),
                Cost = $"${status.Metrics.CostEstimate:F4}"
            },
            Git = status.Git is not null ? new
            {
                status.Git.Branch,
                status.Git.LastCommit,
                status.Git.UncommittedChanges,
                status.Git.UntrackedFiles
            } : null,
            Tests = status.Tests is not null ? new
            {
                status.Tests.Total,
                status.Tests.Passed,
                status.Tests.Failed
            } : null
        };

        return ToolResult.Ok(Name, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
