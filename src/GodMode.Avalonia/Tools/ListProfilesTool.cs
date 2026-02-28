using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class ListProfilesTool(VoiceContext context, IProfileService profileService) : ITool
{
    public string Name => "list_profiles";
    public string Description => "Lists all configured profiles and shows which one is currently active.";
    public IReadOnlyList<ToolParameter> Parameters => [];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var profiles = await profileService.GetProfilesAsync();

        if (profiles.Count == 0)
            return ToolResult.Ok(Name, "No profiles configured. Create one in the app first.");

        var result = profiles.Select(p => new
        {
            p.Name,
            Active = p.Name == context.ActiveProfileName,
            Accounts = p.Accounts.Select(a => new { a.Type, a.Username }).ToList()
        });

        var scope = context.ActiveProfileName ?? "All";
        return ToolResult.Ok(Name,
            $"Active scope: {scope}\n" +
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
