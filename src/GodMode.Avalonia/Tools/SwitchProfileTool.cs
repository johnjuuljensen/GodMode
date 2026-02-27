using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

public sealed class SwitchProfileTool(IProfileService profileService) : ITool
{
    public string Name => "switch_profile";
    public string Description => "Switches the active profile to the specified name.";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new() { Name = "profile_name", Type = "string", Description = "The name of the profile to switch to", Required = true }
    ];

    public async Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        var name = ToolHelper.ExtractString(args, "profile_name");
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Fail(Name, "Missing required parameter: profile_name");

        var profile = await profileService.GetProfileAsync(name);
        if (profile is null)
        {
            var all = await profileService.GetProfilesAsync();
            return ToolResult.Fail(Name,
                $"Profile '{name}' not found. Available: {string.Join(", ", all.Select(p => p.Name))}");
        }

        await profileService.SetSelectedProfileAsync(profile.Name);
        return ToolResult.Ok(Name, $"Switched to profile: {profile.Name}");
    }
}
