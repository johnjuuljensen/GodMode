using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class SetProfileTool(VoiceContext context, IProfileService profileService) : ITool
{
    public string Name => "set_profile";
    public string Description => "Sets the active voice profile scope. Call with no arguments to list profiles. Use 'all' to show everything.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "profile_name", Type = "string", Description = "Profile name to activate, or 'all' for everything", Required = false }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var name = ToolHelper.ExtractString(args, "profile_name");

        // No arg → list profiles
        if (string.IsNullOrWhiteSpace(name))
        {
            var profiles = await profileService.GetProfilesAsync();
            if (profiles.Count == 0)
                return ToolResult.Ok(Name, "No profiles configured.");

            var result = profiles.Select(p => new
            {
                p.Name,
                Active = p.Name == context.ActiveProfileName,
                Accounts = p.Accounts.Select(a => a.Type).ToList()
            });

            var current = context.ActiveProfileName ?? "All";
            return ToolResult.Ok(Name,
                $"Current scope: {current}\n" +
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }

        // "all" → show everything
        if (name.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            await context.SetProfileAsync(null);
            return ToolResult.Ok(Name, "Profile scope set to All (showing everything).");
        }

        // Specific profile
        var profile = await profileService.GetProfileAsync(name);
        if (profile is null)
        {
            var all = await profileService.GetProfilesAsync();
            return ToolResult.Fail(Name,
                $"Profile '{name}' not found. Available: {string.Join(", ", all.Select(p => p.Name))}");
        }

        await context.SetProfileAsync(profile.Name);
        return ToolResult.Ok(Name, $"Profile scope set to: {profile.Name}");
    }
}
