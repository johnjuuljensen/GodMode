using System.Text.Json;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class ListProfilesTool(IProfileService profileService) : ITool
{
    public string Name => "list_profiles";
    public string Description => "Lists all configured profiles and shows which one is currently active.";
    public IReadOnlyList<ToolParameter> Parameters => [];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var profiles = await profileService.GetProfilesAsync();
        var selected = await profileService.GetSelectedProfileAsync();

        if (profiles.Count == 0)
            return ToolResult.Ok(Name, "No profiles configured. Create one in the app first.");

        var result = profiles.Select(p => new
        {
            p.Name,
            Active = p.Name == selected?.Name,
            Accounts = p.Accounts.Select(a => new { a.Type, a.Username }).ToList()
        });

        return ToolResult.Ok(Name, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
