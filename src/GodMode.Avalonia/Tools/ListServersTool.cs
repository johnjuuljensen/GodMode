using System.Text.Json;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class ListServersTool(
    IProfileService profileService,
    IHostConnectionService hostService) : ITool
{
    public string Name => "list_servers";
    public string Description => "Lists all servers/hosts for the current or specified profile, showing their connection state.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "profile_name", Type = "string", Description = "Profile to list servers for (uses current if omitted)", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var profileName = ToolHelper.ExtractString(args, "profile_name");
        var (resolved, found) = await ToolHelper.ResolveProfileAsync(profileService, profileName);
        if (!found)
            return ToolResult.Fail(Name, $"Profile '{profileName}' not found.");

        var hosts = (await hostService.ListAllHostsAsync(resolved)).ToList();
        if (hosts.Count == 0)
            return ToolResult.Ok(Name, $"No servers configured for profile '{resolved}'.");

        var result = hosts.Select(h => new
        {
            h.Name,
            h.Id,
            h.Type,
            State = h.State.ToString(),
            Connected = hostService.IsConnected(resolved, h.Id)
        });

        return ToolResult.Ok(Name, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
