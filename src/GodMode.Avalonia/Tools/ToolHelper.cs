using GodMode.Shared.Models;
using GodMode.Voice.Tools;

namespace GodMode.Avalonia.Tools;

/// <summary>
/// Shared utilities for voice tools that need to resolve names to IDs.
/// </summary>
internal static class ToolHelper
{
    public static string? ExtractString(IDictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is System.Text.Json.JsonElement je) return je.GetString();
        return val?.ToString();
    }

    /// <summary>
    /// Resolves the effective profile name: explicit parameter → selected → first available.
    /// </summary>
    public static async Task<(string Name, bool Found)> ResolveProfileAsync(
        IProfileService profileService, string? explicitName)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            var profile = await profileService.GetProfileAsync(explicitName);
            return profile is not null
                ? (profile.Name, true)
                : (explicitName, false);
        }

        var selected = await profileService.GetSelectedProfileAsync();
        if (selected is not null)
            return (selected.Name, true);

        var all = await profileService.GetProfilesAsync();
        return all.Count > 0
            ? (all[0].Name, true)
            : ("", false);
    }

    /// <summary>
    /// Resolves a host by name within a profile. If no name given, returns the first host.
    /// </summary>
    public static async Task<(HostInfo? Host, string? Error)> ResolveHostAsync(
        IHostConnectionService hostService, string profileName, string? serverName)
    {
        var hosts = (await hostService.ListAllHostsAsync(profileName)).ToList();
        if (hosts.Count == 0)
            return (null, $"No servers found for profile '{profileName}'.");

        if (string.IsNullOrWhiteSpace(serverName))
            return (hosts[0], null);

        var match = hosts.FirstOrDefault(h =>
            h.Name.Contains(serverName, StringComparison.OrdinalIgnoreCase) ||
            h.Id.Contains(serverName, StringComparison.OrdinalIgnoreCase));

        return match is not null
            ? (match, null)
            : (null, $"Server '{serverName}' not found. Available: {string.Join(", ", hosts.Select(h => h.Name))}");
    }

    /// <summary>
    /// Resolves a project by name on a host.
    /// </summary>
    public static async Task<(ProjectSummary? Project, string? Error)> ResolveProjectAsync(
        IProjectService projectService, string profileName, string hostId, string projectName)
    {
        var projects = (await projectService.ListProjectsAsync(profileName, hostId)).ToList();
        if (projects.Count == 0)
            return (null, "No projects found on this server.");

        var match = projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        // Fuzzy: contains match
        match ??= projects.FirstOrDefault(p =>
            p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));

        return match is not null
            ? (match, null)
            : (null, $"Project '{projectName}' not found. Available: {string.Join(", ", projects.Select(p => p.Name))}");
    }

    /// <summary>
    /// Common resolution chain: profile → host → ready to operate.
    /// </summary>
    public static async Task<(string ProfileName, HostInfo Host, string? Error)> ResolveProfileAndHostAsync(
        IProfileService profileService, IHostConnectionService hostService,
        IDictionary<string, object> args)
    {
        var profileName = ExtractString(args, "profile_name");
        var serverName = ExtractString(args, "server_name");

        var (resolvedProfile, profileFound) = await ResolveProfileAsync(profileService, profileName);
        if (!profileFound)
            return ("", null!, $"Profile '{profileName}' not found.");

        var (host, hostError) = await ResolveHostAsync(hostService, resolvedProfile, serverName);
        if (host is null)
            return ("", null!, hostError);

        return (resolvedProfile, host, null);
    }
}
