using GodMode.Avalonia.Voice;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class ResumeProjectTool(VoiceContext context, IProjectService projectService) : ITool
{
    public string Name => "resume_project";
    public string Description => "Resumes a stopped project.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_name", Type = "string", Description = "The name of the project to resume", Required = true }
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
        await projectService.ResumeProjectAsync(match.ProfileName, match.HostId, match.Summary.Id);
        return ToolResult.Ok(Name, $"Project '{match.Summary.Name}' resumed.");
    }
}
