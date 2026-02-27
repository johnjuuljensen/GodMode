namespace GodMode.Voice.Tools;

public sealed class SwitchProfileTool : ITool
{
    public string Name => "switch_profile";
    public string Description => "Switches the active user profile to the specified profile name.";

    public IReadOnlyList<ToolParameter> Parameters => new[]
    {
        new ToolParameter
        {
            Name = "profile_name",
            Type = "string",
            Description = "The name of the profile to switch to (e.g., 'admin', 'default', 'developer').",
            Required = true
        }
    };

    private static readonly HashSet<string> _validProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "default", "admin", "developer", "tester"
    };

    public Task<ToolResult> ExecuteAsync(IDictionary<string, object> args)
    {
        if (!args.TryGetValue("profile_name", out var profileObj) || profileObj is not string profileName
            || string.IsNullOrWhiteSpace(profileName))
        {
            // Try to get it as a JsonElement
            if (profileObj is System.Text.Json.JsonElement jsonElement)
                profileName = jsonElement.GetString() ?? string.Empty;
            else
                return Task.FromResult(ToolResult.Fail(Name, "Missing required parameter: profile_name"));
        }

        if (!_validProfiles.Contains(profileName!))
        {
            return Task.FromResult(ToolResult.Fail(Name,
                $"Unknown profile: '{profileName}'. Valid profiles: {string.Join(", ", _validProfiles)}"));
        }

        GeneralStatusTool.SetProfile(profileName!);
        return Task.FromResult(ToolResult.Ok(Name, $"Switched to profile: {profileName}"));
    }
}
