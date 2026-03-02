using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class ListProfilesTool(VoiceContext context) : ITool
{
    public string Name => "list_profiles";
    public string Description => "Lists all discovered profiles from connected servers and shows which one is currently active.";
    public IReadOnlyList<ToolParameter> Parameters => [];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        await context.EnsureIndexFreshAsync();
        var profiles = context.GetDiscoveredProfileNames();

        if (profiles.Count == 0)
            return ToolResult.Ok(Name, "No profiles discovered. Connect to a server first.");

        var result = profiles.Select(p => new
        {
            Name = p,
            Active = p.Equals(context.ActiveProfileName, StringComparison.OrdinalIgnoreCase),
            ProjectCount = context.ProjectIndex.Count(ip => ip.ProfileName.Equals(p, StringComparison.OrdinalIgnoreCase))
        });

        var scope = context.ActiveProfileName ?? "All";
        return ToolResult.Ok(Name,
            $"Active scope: {scope}\n" +
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
