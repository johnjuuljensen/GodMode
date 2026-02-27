using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class SendInputTool(
    IProfileService profileService,
    IHostConnectionService hostService,
    IProjectService projectService) : ITool
{
    public string Name => "send_input";
    public string Description => "Sends a text response to a project that is waiting for input.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "project_name", Type = "string", Description = "The name of the project to send input to", Required = true },
        new() { Name = "message", Type = "string", Description = "The text message to send as input", Required = true },
        new() { Name = "server_name", Type = "string", Description = "Server the project is on (uses first available if omitted)", Required = false },
        new() { Name = "profile_name", Type = "string", Description = "Profile to use (uses current if omitted)", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var projectName = ToolHelper.ExtractString(args, "project_name");
        if (string.IsNullOrWhiteSpace(projectName))
            return ToolResult.Fail(Name, "Missing required parameter: project_name");

        var message = ToolHelper.ExtractString(args, "message");
        if (string.IsNullOrWhiteSpace(message))
            return ToolResult.Fail(Name, "Missing required parameter: message");

        var (profileName, host, resolveError) = await ToolHelper.ResolveProfileAndHostAsync(
            profileService, hostService, args);
        if (resolveError is not null)
            return ToolResult.Fail(Name, resolveError);

        var (project, projectError) = await ToolHelper.ResolveProjectAsync(
            projectService, profileName, host.Id, projectName);
        if (project is null)
            return ToolResult.Fail(Name, projectError!);

        await projectService.SendInputAsync(profileName, host.Id, project.Id, message);
        return ToolResult.Ok(Name, $"Input sent to project '{project.Name}': {message}");
    }
}
