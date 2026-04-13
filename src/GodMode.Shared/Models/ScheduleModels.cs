using System.Text.Json;

namespace GodMode.Shared.Models;

// ── Schedule Definition (stored in .profiles/{name}/schedules/{schedule-name}.json) ──

/// <summary>
/// A schedule definition that triggers project creation on a cron pattern.
/// </summary>
public record ScheduleConfig(
    string? Description = null,
    bool Enabled = true,
    string Cron = "0 9 * * 1-5",
    ScheduleTarget? Target = null);

/// <summary>
/// What a schedule triggers when it fires.
/// </summary>
public record ScheduleTarget(
    string? RootName = null,
    string? ActionName = null,
    Dictionary<string, JsonElement>? Inputs = null,
    bool ReuseProject = false);

// ── Schedule info for UI ──

/// <summary>
/// Wire type for listing schedules in a profile.
/// </summary>
public record ScheduleInfo(
    string Name,
    string ProfileName,
    string? Description,
    bool Enabled,
    string Cron,
    ScheduleTarget? Target,
    string? NextRunDisplay = null);
