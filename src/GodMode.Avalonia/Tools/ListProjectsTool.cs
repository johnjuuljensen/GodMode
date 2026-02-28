using System.Text.Json;
using GodMode.Avalonia.Voice;
using GodMode.Voice.Tools;

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

        var result = context.ProjectIndex
            .OrderByDescending(p => p.Summary.UpdatedAt)
            .Select(p => new
            {
                p.Summary.Name,
                State = p.Summary.State.ToString(),
                p.Summary.UpdatedAt,
                Server = p.HostName,
                WaitingForInput = p.Summary.CurrentQuestion is not null,
                Question = p.Summary.CurrentQuestion
            });

        return ToolResult.Ok(Name, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
