using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

// The accounts' usage on the roots and profiles (#600): which account each runs on, its ceiling, and the work a root holds
// while its account is above it
public partial class ProjectManager
{
    public AccountUsage[] GetUsage() => _lifecycle.AccountLimits.Usages();

    /// <summary>The account a profile's sessions run on: its environment's config dir.</summary>
    private static string ProfileAccount(string profileName, ProfileConfig profile) =>
        ConfigDirOf(MergeAndExpandEnvironment(profile.Environment, null, profileName) ?? []);

    /// <summary>
    /// The account a root's sessions of <paramref name="action"/> run on, as their launch would: the action's environment
    /// over the profile's. With no action, the root's <c>config.json</c>'s environment.
    /// </summary>
    private string RootAccount(ProfileSnapshot snap, string profileName, RootConfig config, CreateAction? action) =>
        ConfigDirOf(MergeAndExpandEnvironment(ProfileEnvironment(snap, profileName), action != null ? action.Environment : config.Environment,
            profileName, config.StripEnvVarProfile) ?? []);

    /// <summary>The root's usage ceiling: its own, window by window over its profile's; null when neither sets one.</summary>
    private UsageCeiling? CeilingOf(ProfileSnapshot snap, string profileName, RootConfig config)
    {
        var profile = snap.Profiles.TryGetValue(profileName, out var settings) ? settings.UsageCeiling
            : RootSources.From(_configuration).Profiles.TryGetValue(profileName, out var configured) ? configured.UsageCeiling
            : null;
        return config.UsageCeiling?.Over(profile) ?? profile;
    }

    /// <summary>
    /// Why the root starts and resumes nothing now (issue #600), null when it may: its account's last reading of a window is
    /// at or above the root's ceiling for it, and the window has not reset since. An account with no reading never holds.
    /// </summary>
    private string? WhyOverCeiling(string rootName, string account, UsageCeiling? ceiling)
    {
        if (ceiling == null || _lifecycle.AccountLimits.UsageOf(account) is not { } usage) return null;
        var now = DateTime.UtcNow;
        return Over(AccountLimits.SessionWindow, usage.Session, ceiling.Session) ?? Over(AccountLimits.WeeklyWindow, usage.Weekly, ceiling.Weekly);

        string? Over(string name, UsageWindow? window, double? limit) =>
            window is { } reading && limit is { } most && reading.Utilization >= most && !(reading.ResetsAt <= now)
                ? RateLimitTexts.OverCeiling(rootName, usage.Name, name, reading.Utilization, most, reading.ResetsAt, now)
                : null;
    }

    /// <summary>
    /// Refuses, saying why, a create of <paramref name="action"/> in the root while its account is above the root's ceiling
    /// (<see cref="WhyOverCeiling"/>).
    /// </summary>
    private void RefuseCreateOverCeiling(ProfileSnapshot snap, string profileName, string rootName, RootConfig config, CreateAction action)
    {
        if (WhyOverCeiling(rootName, RootAccount(snap, profileName, config, action), CeilingOf(snap, profileName, config)) is { } why)
            throw new InvalidOperationException(why);
    }

    /// <summary>
    /// Refuses, saying why, a resume of the session while its account is above its root's ceiling (<see cref="WhyOverCeiling"/>).
    /// A session whose claude runs is left alone; a root whose config cannot be read sets no ceiling here, and its launch says why.
    /// </summary>
    private void RefuseResumeOverCeiling(ProjectInfo project)
    {
        if (_lifecycle.IsRunning(project) || project.Status.RootName is not { } rootName) return;
        var profileName = project.ProfileName ?? project.Status.ProfileName ?? "";
        UsageCeiling? ceiling;
        try { ceiling = CeilingOf(_snapshot, profileName, _rootConfigReader.ReadConfigStrict(project.RootPath)); }
        catch (Exception) { return; }
        if (WhyOverCeiling(rootName, AccountOf(project), ceiling) is { } why)
            throw new InvalidOperationException(why);
    }
}
