using System.Text.Json;
using GodMode.Avalonia.Voice;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class ProjectStatusTool(VoiceContext context, IProjectService projectService) : ITool
{
    public string Name => "project_status";
    public string Description => "Gets detailed status of a project including state, metrics, git info, and any pending question.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_name", Type = "string", Description = "The name of the project to check", Required = true }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var projectName = ToolHelper.ExtractString(args, "project_name");
        if (string.IsNullOrWhiteSpace(projectName))
            return ToolResult.Fail(Name, "Missing required parameter: project_name");

        await context.EnsureIndexFreshAsync();
        var result = context.ResolveProject(projectName);

        if (result.Candidates is not null)
        {
            context.SetPendingDisambiguation(new DisambiguationState
            {
                Options = result.Candidates,
                OriginalToolName = Name,
                OriginalArgs = args,
                ProjectParamName = "project_name"
            });
            return ToolResult.Ok(Name, ToolHelper.FormatDisambiguation(result.Candidates));
        }

        if (!result.Resolved)
            return ToolResult.Fail(Name, result.Error!);

        var match = result.Match!;
        var status = await projectService.GetStatusAsync(match.ProfileName, match.HostId, match.Summary.Id);

        var output = new
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

        return ToolResult.Ok(Name, JsonSerializer.Serialize(output,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
