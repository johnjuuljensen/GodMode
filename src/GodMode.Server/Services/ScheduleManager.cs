using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using GodMode.Server.Hubs;
using GodMode.Shared.Hubs;
using GodMode.Shared.Models;
using Microsoft.AspNetCore.SignalR;

namespace GodMode.Server.Services;

/// <summary>
/// Manages file-based schedules under .profiles/{name}/schedules/.
/// Each schedule is a JSON file. CRUD = file operations.
/// Uses cron expressions internally with Timer-based triggering.
/// </summary>
public class ScheduleManager : IDisposable
{
    private readonly IProjectManager _projectManager;
    private readonly IHubContext<ProjectHub, IProjectHubClient> _hubContext;
    private readonly ILogger<ScheduleManager> _logger;
    private readonly string _projectRootsDir;

    private readonly ConcurrentDictionary<string, ScheduleTimer> _timers = new();
    private readonly CancellationTokenSource _cts = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class ScheduleTimer : IDisposable
    {
        public string ProfileName { get; init; } = "";
        public string ScheduleName { get; init; } = "";
        public ScheduleConfig Config { get; set; } = new();
        public Timer? Timer { get; set; }
        public void Dispose() => Timer?.Dispose();
    }

    public ScheduleManager(
        IProjectManager projectManager,
        IHubContext<ProjectHub, IProjectHubClient> hubContext,
        IConfiguration configuration,
        ILogger<ScheduleManager> logger)
    {
        _projectManager = projectManager;
        _hubContext = hubContext;
        _logger = logger;
        _projectRootsDir = Path.GetFullPath(configuration["ProjectRootsDir"] ?? "roots");
    }

    /// <summary>
    /// Scans all profiles and registers timers for enabled schedules.
    /// </summary>
    public void Initialize()
    {
        var profilesDir = Path.Combine(_projectRootsDir, ".profiles");
        if (!Directory.Exists(profilesDir)) return;

        foreach (var profileDir in Directory.GetDirectories(profilesDir))
        {
            var profileName = Path.GetFileName(profileDir);
            var schedulesDir = Path.Combine(profileDir, "schedules");
            if (!Directory.Exists(schedulesDir)) continue;

            foreach (var file in Directory.GetFiles(schedulesDir, "*.json"))
            {
                var scheduleName = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var config = ReadScheduleFile(file);
                    if (config != null)
                        RegisterTimer(profileName, scheduleName, config);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load schedule {Profile}/{Name}", profileName, scheduleName);
                }
            }
        }

        _logger.LogInformation("Schedule manager initialized: {Count} schedules loaded", _timers.Count);
    }

    /// <summary>
    /// Lists all schedules for a profile.
    /// </summary>
    public List<ScheduleInfo> GetSchedules(string profileName)
    {
        var schedulesDir = GetSchedulesDir(profileName);
        if (!Directory.Exists(schedulesDir)) return [];

        var result = new List<ScheduleInfo>();
        foreach (var file in Directory.GetFiles(schedulesDir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var config = ReadScheduleFile(file);
            if (config != null)
            {
                var nextRun = config.Enabled ? GetNextRunDisplay(config.Cron) : null;
                result.Add(new ScheduleInfo(name, profileName, config.Description, config.Enabled,
                    config.Cron, config.Target, nextRun));
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a new schedule.
    /// </summary>
    public ScheduleInfo CreateSchedule(string profileName, string name, ScheduleConfig config)
    {
        ValidateName(name);
        var dir = GetSchedulesDir(profileName);
        var path = Path.Combine(dir, $"{name}.json");
        if (File.Exists(path))
            throw new InvalidOperationException($"Schedule '{name}' already exists in profile '{profileName}'.");

        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));

        if (config.Enabled)
            RegisterTimer(profileName, name, config);

        _logger.LogInformation("Created schedule {Profile}/{Name}", profileName, name);

        var nextRun = config.Enabled ? GetNextRunDisplay(config.Cron) : null;
        return new ScheduleInfo(name, profileName, config.Description, config.Enabled,
            config.Cron, config.Target, nextRun);
    }

    /// <summary>
    /// Updates a schedule (overwrites the file).
    /// </summary>
    public ScheduleInfo UpdateSchedule(string profileName, string name, ScheduleConfig config)
    {
        var path = GetSchedulePath(profileName, name);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"Schedule '{name}' not found in profile '{profileName}'.");

        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));

        // Re-register timer
        var key = $"{profileName}/{name}";
        if (_timers.TryRemove(key, out var existing))
            existing.Dispose();

        if (config.Enabled)
            RegisterTimer(profileName, name, config);

        _logger.LogInformation("Updated schedule {Profile}/{Name}", profileName, name);

        var nextRun = config.Enabled ? GetNextRunDisplay(config.Cron) : null;
        return new ScheduleInfo(name, profileName, config.Description, config.Enabled,
            config.Cron, config.Target, nextRun);
    }

    /// <summary>
    /// Deletes a schedule.
    /// </summary>
    public void DeleteSchedule(string profileName, string name)
    {
        var path = GetSchedulePath(profileName, name);
        if (File.Exists(path))
            File.Delete(path);

        var key = $"{profileName}/{name}";
        if (_timers.TryRemove(key, out var existing))
            existing.Dispose();

        _logger.LogInformation("Deleted schedule {Profile}/{Name}", profileName, name);
    }

    /// <summary>
    /// Toggles the enabled state of a schedule.
    /// </summary>
    public ScheduleInfo ToggleSchedule(string profileName, string name, bool enabled)
    {
        var path = GetSchedulePath(profileName, name);
        if (!File.Exists(path))
            throw new KeyNotFoundException($"Schedule '{name}' not found in profile '{profileName}'.");

        var config = ReadScheduleFile(path) ?? new ScheduleConfig();
        config = config with { Enabled = enabled };

        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));

        var key = $"{profileName}/{name}";
        if (_timers.TryRemove(key, out var existing))
            existing.Dispose();

        if (enabled)
            RegisterTimer(profileName, name, config);

        _logger.LogInformation("Toggled schedule {Profile}/{Name} → {Enabled}", profileName, name, enabled);

        var nextRun = enabled ? GetNextRunDisplay(config.Cron) : null;
        return new ScheduleInfo(name, profileName, config.Description, enabled,
            config.Cron, config.Target, nextRun);
    }

    // ── Internal ──

    private void RegisterTimer(string profileName, string name, ScheduleConfig config)
    {
        if (!config.Enabled || config.Target == null) return;

        var key = $"{profileName}/{name}";
        var delay = GetNextRunDelay(config.Cron);
        if (delay == null) return;

        var st = new ScheduleTimer
        {
            ProfileName = profileName,
            ScheduleName = name,
            Config = config,
        };

        st.Timer = new Timer(async _ => await OnTimerFired(key), null, delay.Value, Timeout.InfiniteTimeSpan);
        _timers[key] = st;

        _logger.LogDebug("Registered timer for {Key}, next fire in {Delay}", key, delay);
    }

    private async Task OnTimerFired(string key)
    {
        if (!_timers.TryGetValue(key, out var st)) return;

        _logger.LogInformation("Schedule fired: {Key}", key);

        try
        {
            var target = st.Config.Target;
            if (target == null) return;

            // Resolve date placeholders in inputs
            var inputs = ResolveInputPlaceholders(target.Inputs);
            // Schedules always auto-suffix to avoid folder conflicts
            inputs["__autoSuffix"] = JsonSerializer.SerializeToElement(true);

            if (string.IsNullOrEmpty(target.RootName)) return;

            var request = new CreateProjectRequest(st.ProfileName, target.RootName, inputs, target.ActionName);
            var status = await _projectManager.CreateProjectAsync(request);
            await _hubContext.Clients.All.ProjectCreated(status);

            _logger.LogInformation("Schedule {Key} triggered project: {ProjectId}", key, status.Id);
            await _hubContext.Clients.All.ScheduleTriggered(st.ProfileName, st.ScheduleName, status.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Schedule {Key} failed", key);
        }
        finally
        {
            // Re-register for next run
            var delay = GetNextRunDelay(st.Config.Cron);
            if (delay != null)
                st.Timer?.Change(delay.Value, Timeout.InfiniteTimeSpan);
        }
    }

    private static Dictionary<string, JsonElement> ResolveInputPlaceholders(Dictionary<string, JsonElement>? inputs)
    {
        if (inputs == null) return new();

        var now = DateTime.UtcNow;
        var resolved = new Dictionary<string, JsonElement>();

        foreach (var (key, value) in inputs)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                var str = value.GetString() ?? "";
                str = str.Replace("{date}", now.ToString("yyyy-MM-dd"))
                         .Replace("{time}", now.ToString("HH:mm"))
                         .Replace("{datetime}", now.ToString("yyyy-MM-dd-HH:mm"));
                resolved[key] = JsonSerializer.SerializeToElement(str);
            }
            else
            {
                resolved[key] = value;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Simple cron parser — computes delay until next run.
    /// Supports: minute hour day-of-month month day-of-week
    /// Common patterns: "0 9 * * 1-5" (weekdays at 9am), "*/30 * * * *" (every 30 min)
    /// </summary>
    private static TimeSpan? GetNextRunDelay(string cron)
    {
        var next = GetNextRunTime(cron, DateTime.UtcNow);
        if (next == null) return null;
        var delay = next.Value - DateTime.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Computes the next UTC time matching a cron expression.
    /// </summary>
    internal static DateTime? GetNextRunTime(string cron, DateTime from)
    {
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;

        var minutes = ParseCronField(parts[0], 0, 59);
        var hours = ParseCronField(parts[1], 0, 23);
        var daysOfMonth = ParseCronField(parts[2], 1, 31);
        var months = ParseCronField(parts[3], 1, 12);
        var daysOfWeek = ParseCronField(parts[4], 0, 6);

        if (minutes == null || hours == null || daysOfMonth == null || months == null || daysOfWeek == null)
            return null;

        var candidate = from.AddMinutes(1);
        candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, candidate.Minute, 0, DateTimeKind.Utc);

        // Search up to 366 days ahead
        for (int i = 0; i < 527040; i++) // 366 days * 24h * 60m
        {
            if (months.Contains(candidate.Month) &&
                daysOfMonth.Contains(candidate.Day) &&
                daysOfWeek.Contains((int)candidate.DayOfWeek) &&
                hours.Contains(candidate.Hour) &&
                minutes.Contains(candidate.Minute))
            {
                return candidate;
            }
            candidate = candidate.AddMinutes(1);
        }

        return null;
    }

    private static HashSet<int>? ParseCronField(string field, int min, int max)
    {
        var values = new HashSet<int>();

        foreach (var part in field.Split(','))
        {
            var trimmed = part.Trim();

            // Wildcard
            if (trimmed == "*")
            {
                for (int i = min; i <= max; i++) values.Add(i);
                continue;
            }

            // Step: */N or N-M/S
            var stepMatch = Regex.Match(trimmed, @"^(\*|(\d+)-(\d+))/(\d+)$");
            if (stepMatch.Success)
            {
                var step = int.Parse(stepMatch.Groups[4].Value);
                int start = min, end = max;
                if (stepMatch.Groups[2].Success)
                {
                    start = int.Parse(stepMatch.Groups[2].Value);
                    end = int.Parse(stepMatch.Groups[3].Value);
                }
                for (int i = start; i <= end; i += step) values.Add(i);
                continue;
            }

            // Range: N-M
            var rangeMatch = Regex.Match(trimmed, @"^(\d+)-(\d+)$");
            if (rangeMatch.Success)
            {
                var s = int.Parse(rangeMatch.Groups[1].Value);
                var e = int.Parse(rangeMatch.Groups[2].Value);
                for (int i = s; i <= e; i++) values.Add(i);
                continue;
            }

            // Single value
            if (int.TryParse(trimmed, out var val))
            {
                values.Add(val);
                continue;
            }

            return null; // invalid field
        }

        return values;
    }

    private static string? GetNextRunDisplay(string cron)
    {
        var next = GetNextRunTime(cron, DateTime.UtcNow);
        if (next == null) return null;
        var diff = next.Value - DateTime.UtcNow;
        if (diff.TotalMinutes < 60) return $"in {(int)diff.TotalMinutes}m";
        if (diff.TotalHours < 24) return $"in {(int)diff.TotalHours}h";
        return $"in {(int)diff.TotalDays}d";
    }

    private string GetSchedulesDir(string profileName) =>
        Path.Combine(_projectRootsDir, ".profiles", profileName, "schedules");

    private string GetSchedulePath(string profileName, string name) =>
        Path.Combine(GetSchedulesDir(profileName), $"{name}.json");

    private static ScheduleConfig? ReadScheduleFile(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ScheduleConfig>(json, JsonOptions);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Schedule name cannot be empty.");
        if (name.Length > 64)
            throw new ArgumentException("Schedule name must be 64 characters or fewer.");
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                throw new ArgumentException($"Schedule name contains invalid character: '{c}'.");
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var timer in _timers.Values)
            timer.Dispose();
        _timers.Clear();
        _cts.Dispose();
    }
}
