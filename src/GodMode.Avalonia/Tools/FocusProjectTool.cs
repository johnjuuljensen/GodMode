using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class FocusProjectTool(VoiceContext context) : ITool
{
    public string Name => "focus_project";
    public string Description => "Focuses on a project to see its live output and send it commands directly.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_name", Type = "string", Description = "The name of the project to focus on", Required = true }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var projectName = ToolHelper.ExtractString(args, "project_name");
        if (string.IsNullOrWhiteSpace(projectName))
            return ToolResult.Fail(Name, "Missing required parameter: project_name");

        // Already focused on the same project?
        if (context.Focus is not null &&
            context.Focus.ProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Ok(Name, $"Already focused on '{context.Focus.ProjectName}'.");

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
        await context.FocusProjectAsync(match.ProfileName, match.HostId, match.Summary.Id, match.Summary.Name);

        return ToolResult.Ok(Name,
            $"Now focused on '{match.Summary.Name}' ({match.Summary.State}). " +
            $"You'll see live output. Say 'unfocus' to exit.");
    }
}
