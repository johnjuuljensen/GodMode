using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class CreateProjectTool(VoiceContext context, IProjectService projectService) : ITool
{
    public string Name => "create_project";
    public string Description => "Creates a new project on a server. Lists available project roots if no root is specified.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_root", Type = "string", Description = "The project root/template to use", Required = false },
        new() { Name = "name", Type = "string", Description = "Name for the new project", Required = false },
        new() { Name = "prompt", Type = "string", Description = "Initial prompt or description for the project", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var (profileName, host, error) = await ResolveHostFromContextAsync();
        if (error is not null)
            return ToolResult.Fail(Name, error);

        var rootName = ToolHelper.ExtractString(args, "project_root");

        // If no root specified, list available roots
        if (string.IsNullOrWhiteSpace(rootName))
        {
            var roots = (await projectService.ListProjectRootsAsync(profileName, host!.Id)).ToList();
            if (roots.Count == 0)
                return ToolResult.Fail(Name, "No project roots configured on this server.");

            if (roots.Count == 1)
            {
                rootName = roots[0].Name;
            }
            else
            {
                var rootList = roots.Select(r => new { r.Name, r.Description });
                return ToolResult.Ok(Name,
                    $"Multiple project roots available. Specify one:\n" +
                    JsonSerializer.Serialize(rootList, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        var inputs = new Dictionary<string, JsonElement>();
        var name = ToolHelper.ExtractString(args, "name");
        var prompt = ToolHelper.ExtractString(args, "prompt");

        if (!string.IsNullOrWhiteSpace(name))
            inputs["name"] = JsonSerializer.SerializeToElement(name);
        if (!string.IsNullOrWhiteSpace(prompt))
            inputs["prompt"] = JsonSerializer.SerializeToElement(prompt);

        var detail = await projectService.CreateProjectAsync(profileName, host!.Id, rootName, inputs);

        // Refresh index to include the new project
        await context.RefreshProjectIndexAsync();

        return ToolResult.Ok(Name,
            $"Project '{detail.Status.Name}' created successfully. State: {detail.Status.State}");
    }

    private async Task<(string ProfileName, GodMode.Shared.Models.HostInfo? Host, string? Error)> ResolveHostFromContextAsync()
    {
        var (profileName, profileFound) = await context.ResolveEffectiveProfileAsync();
        if (!profileFound) return ("", null, "No profile available.");

        var (host, hostError) = await context.ResolveEffectiveHostAsync();
        return (profileName, host, hostError);
    }
}
