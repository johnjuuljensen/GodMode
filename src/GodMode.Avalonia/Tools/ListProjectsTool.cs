using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class ListProjectsTool(VoiceContext context) : ITool
{
    public string Name => "list_projects";
    public string Description => "Lists all projects in the current scope, showing their name, state, and any pending questions.";
    public IReadOnlyList<ToolParameter> Parameters => [];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        await context.EnsureIndexFreshAsync();

        if (context.ProjectIndex.Count == 0)
            return ToolResult.Ok(Name, "No projects found in the current scope.");

        var activeProfile = context.ActiveProfileName;
        var result = context.ProjectIndex
            .OrderByDescending(p => p.Summary.UpdatedAt)
            .Select(p => new
            {
                Profile = activeProfile is null ? p.ProfileName : null,
                Root = p.Summary.RootName,
                p.Summary.Name,
                State = p.Summary.State.ToString(),
                p.Summary.UpdatedAt,
                Server = p.HostName,
                WaitingForInput = p.Summary.CurrentQuestion is not null,
                Question = p.Summary.CurrentQuestion
            });

        var scope = activeProfile is not null ? $"Profile: {activeProfile}" : "All profiles";
        return ToolResult.Ok(Name,
            $"Scope: {scope}\n" +
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
