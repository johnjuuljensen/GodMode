using System.Text.Json;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class CreateProjectTool(
    IProfileService profileService,
    IHostConnectionService hostService,
    IProjectService projectService) : ITool
{
    public string Name => "create_project";
    public string Description => "Creates a new project on a server. Lists available project roots if no root is specified.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_root", Type = "string", Description = "The project root/template to use", Required = false },
        new() { Name = "name", Type = "string", Description = "Name for the new project", Required = false },
        new() { Name = "prompt", Type = "string", Description = "Initial prompt or description for the project", Required = false },
        new() { Name = "server_name", Type = "string", Description = "Server to create on (uses first available if omitted)", Required = false },
        new() { Name = "profile_name", Type = "string", Description = "Profile to use (uses current if omitted)", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var (profileName, host, resolveError) = await ToolHelper.ResolveProfileAndHostAsync(
            profileService, hostService, args);
        if (resolveError is not null)
            return ToolResult.Fail(Name, resolveError);

        var rootName = ToolHelper.ExtractString(args, "project_root");

        // If no root specified, list available roots
        if (string.IsNullOrWhiteSpace(rootName))
        {
            var roots = (await projectService.ListProjectRootsAsync(profileName, host.Id)).ToList();
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

        // Build inputs from provided parameters
        var inputs = new Dictionary<string, JsonElement>();
        var name = ToolHelper.ExtractString(args, "name");
        var prompt = ToolHelper.ExtractString(args, "prompt");

        if (!string.IsNullOrWhiteSpace(name))
            inputs["name"] = JsonSerializer.SerializeToElement(name);
        if (!string.IsNullOrWhiteSpace(prompt))
            inputs["prompt"] = JsonSerializer.SerializeToElement(prompt);

        var detail = await projectService.CreateProjectAsync(profileName, host.Id, rootName, inputs);

        return ToolResult.Ok(Name,
            $"Project '{detail.Status.Name}' created successfully. State: {detail.Status.State}");
    }
}
