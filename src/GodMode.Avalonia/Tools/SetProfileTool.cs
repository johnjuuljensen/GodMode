using System.Text.Json;
using GodMode.Avalonia.Voice;

namespace GodMode.Avalonia.Tools;

public sealed class SetProfileTool(VoiceContext context) : ITool
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

        // No arg → list discovered profiles
        if (string.IsNullOrWhiteSpace(name))
        {
            await context.EnsureIndexFreshAsync();
            var profiles = context.GetDiscoveredProfileNames();
            if (profiles.Count == 0)
                return ToolResult.Ok(Name, "No profiles discovered from connected servers.");

            var current = context.ActiveProfileName ?? "All";
            var result = profiles.Select(p => new
            {
                Name = p,
                Active = p.Equals(context.ActiveProfileName, StringComparison.OrdinalIgnoreCase)
            });

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

        // Specific profile — validate against discovered names
        await context.EnsureIndexFreshAsync();
        var discovered = context.GetDiscoveredProfileNames();
        var match = discovered.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return ToolResult.Fail(Name,
                $"Profile '{name}' not found. Available: {string.Join(", ", discovered)}");
        }

        await context.SetProfileAsync(match);
        return ToolResult.Ok(Name, $"Profile scope set to: {match}");
    }
}
