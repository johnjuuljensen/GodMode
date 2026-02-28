using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class SetServerTool(VoiceContext context, IHostConnectionService hostService) : ITool
{
    public string Name => "set_server";
    public string Description => "Sets the active server within the current profile. Call with no arguments to list servers. Use 'all' for auto-select.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "server_name", Type = "string", Description = "Server name to activate, or 'all' for auto-select", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var name = ToolHelper.ExtractString(args, "server_name");

        var (profileName, profileFound) = await context.ResolveEffectiveProfileAsync();
        if (!profileFound)
            return ToolResult.Fail(Name, "No profile available. Set a profile first.");

        var hosts = (await hostService.ListAllHostsAsync(profileName)).ToList();

        // No arg → list servers
        if (string.IsNullOrWhiteSpace(name))
        {
            if (hosts.Count == 0)
                return ToolResult.Ok(Name, $"No servers for profile '{profileName}'.");

            var result = hosts.Select(h => new
            {
                h.Name,
                h.Id,
                h.Type,
                State = h.State.ToString(),
                Active = h.Id == context.ActiveHostId,
                Connected = hostService.IsConnected(profileName, h.Id)
            });

            var current = context.ActiveHostName ?? "Auto";
            return ToolResult.Ok(Name,
                $"Current server: {current}\n" +
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }

        // "all" → auto-select
        if (name.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            await context.SetHostAsync(null);
            return ToolResult.Ok(Name, "Server scope set to auto-select.");
        }

        // Find by name
        var match = hosts.FirstOrDefault(h =>
            h.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            h.Id.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return ToolResult.Fail(Name,
                $"Server '{name}' not found. Available: {string.Join(", ", hosts.Select(h => h.Name))}");

        await context.SetHostAsync(match.Id, match.Name);
        return ToolResult.Ok(Name, $"Active server set to: {match.Name}");
    }
}
