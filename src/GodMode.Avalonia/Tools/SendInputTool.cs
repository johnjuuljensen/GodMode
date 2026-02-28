using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class SendInputTool(VoiceContext context, IProjectService projectService) : ITool
{
    public string Name => "send_input";
    public string Description => "Sends a text response to a project. Uses the focused project if one is active.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_name", Type = "string", Description = "The project name (optional if a project is focused)", Required = false },
        new() { Name = "message", Type = "string", Description = "The text message to send", Required = true }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var message = ToolHelper.ExtractString(args, "message");
        if (string.IsNullOrWhiteSpace(message))
            return ToolResult.Fail(Name, "Missing required parameter: message");

        var projectName = ToolHelper.ExtractString(args, "project_name");

        // If no project name and we have focus, use the focused project
        if (string.IsNullOrWhiteSpace(projectName) && context.Focus is not null)
        {
            await projectService.SendInputAsync(
                context.Focus.ProfileName, context.Focus.HostId, context.Focus.ProjectId, message);
            return ToolResult.Ok(Name, $"Sent to '{context.Focus.ProjectName}': {message}");
        }

        if (string.IsNullOrWhiteSpace(projectName))
            return ToolResult.Fail(Name, "No project specified and no project is focused.");

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
        await projectService.SendInputAsync(match.ProfileName, match.HostId, match.Summary.Id, message);
        return ToolResult.Ok(Name, $"Sent to '{match.Summary.Name}': {message}");
    }
}
