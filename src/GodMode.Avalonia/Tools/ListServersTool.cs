using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class ListServersTool(VoiceContext context, IHostConnectionService hostService) : ITool
{
    public string Name => "list_servers";
    public string Description => "Lists all servers for the active profile, showing their connection state.";
    public IReadOnlyList<ToolParameter> Parameters => [];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var (profileName, found) = await context.ResolveEffectiveProfileAsync();
        if (!found)
            return ToolResult.Fail(Name, "No profile available.");

        var hosts = (await hostService.ListAllHostsAsync(profileName)).ToList();
        if (hosts.Count == 0)
            return ToolResult.Ok(Name, $"No servers for profile '{profileName}'.");

        var result = hosts.Select(h => new
        {
            h.Name,
            h.Type,
            State = h.State.ToString(),
            Active = h.Id == context.ActiveHostId,
            Connected = hostService.IsConnected(profileName, h.Id)
        });

        return ToolResult.Ok(Name, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
