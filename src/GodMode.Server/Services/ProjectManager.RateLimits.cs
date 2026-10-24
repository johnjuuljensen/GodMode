using GodMode.Server.Models;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

// Sessions an account's rate limit holds (#596): what is sent to them waits for the reset, and at the reset the server
// sends them on, from the schedule each keeps in its status.json (ProjectStatus.RateLimited)
public partial class ProjectManager
{
    /// <summary>How long after a limit's reset the server sends its sessions on, in seconds: claude's reset is not to the second.</summary>
    public const string ResumeMarginSetting = "RateLimits:ResumeMarginSeconds";

    private TimeSpan ResumeMargin => TimeSpan.FromSeconds(_configuration.GetValue(ResumeMarginSetting, 30.0));

    /// <summary>A session's resume at its limit's reset, as scheduled: for which hold, and what cancels it.</summary>
    private sealed record LimitResume(ProjectInfo Project, DateTime Until, CancellationTokenSource Cancel);

    private readonly Dictionary<string, LimitResume> _limitResumes = new();
    private readonly Lock _limitResumesLock = new();

    /// <summary>
    /// Makes the session's resume match its hold (<see cref="ProjectStatus.RateLimited"/>): scheduled at its reset, plus
    /// <see cref="ResumeMargin"/>, when it has one, and none when it has not. After every status push, and for each
    /// session a start recovers.
    /// </summary>
    private void ScheduleLimitResume(ProjectInfo project)
    {
        var id = project.Status.Id;
        var hold = IsTracked(project) ? project.Status.RateLimited : null;
        lock (_limitResumesLock)
        {
            if (_limitResumes.TryGetValue(id, out var scheduled))
            {
                if (hold != null && scheduled.Until == hold.Until && ReferenceEquals(scheduled.Project, project)) return;
                scheduled.Cancel.Cancel();
                _limitResumes.Remove(id);
            }
            if (hold == null) return;
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            var resume = new LimitResume(project, hold.Until, cancel);
            _limitResumes[id] = resume;
            _ = ResumeAtResetAsync(resume);
        }
    }

    private async Task ResumeAtResetAsync(LimitResume resume)
    {
        var id = resume.Project.Status.Id;
        try
        {
            if (resume.Until + ResumeMargin - DateTime.UtcNow is { Ticks: > 0 } wait) await Task.Delay(wait, resume.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
            // Replaced, cleared or the server is stopping: whoever cancelled it has taken it out
            resume.Cancel.Dispose();
            return;
        }
        lock (_limitResumesLock)
        {
            if (!_limitResumes.TryGetValue(id, out var current) || current != resume) return;
            _limitResumes.Remove(id);
        }
        try { await ResumeAfterLimitAsync(resume.Project, resume.Until); }
        catch (Exception ex) { _logger.LogError(ex, "Could not send project {ProjectId} on after its account's rate limit reset", id); }
        finally { resume.Cancel.Dispose(); }
    }

    /// <summary>
    /// The limit that held the session has reset: its hold is cleared, and it is sent what the user sent it meanwhile, or
    /// its create's prompt, else, when the limit stopped its turn, the continuation (<see cref="RateLimitTexts.Continuation"/>),
    /// with what was held for it in its inbox. A session whose claude has exited is resumed with it. One whose account
    /// is limited again, until later, waits for that. The account's reset is told once, by the session that told its hit.
    /// </summary>
    private async Task ResumeAfterLimitAsync(ProjectInfo project, DateTime until)
    {
        var id = project.Status.Id;
        if (!IsTracked(project) || project.Status.RateLimited is not { } hold || hold.Until != until) return;
        var account = AccountOf(project);
        var now = DateTime.UtcNow;
        if (_lifecycle.AccountLimits.LimitOf(account, now) is { } later && later.Until > until)
        {
            _logger.LogInformation("Project {ProjectId}: its account is limited again, until {Until:u}; it waits for that", id, later.Until);
            await _lifecycle.UpdateStatusAsync(project, status => status with
            {
                RateLimited = status.RateLimited is { } kept ? kept with { Until = later.Until, Window = later.Window } : null,
            });
            await NotifyStatusChanged(project);
            return;
        }

        await _lifecycle.UpdateStatusAsync(project, status => status with { RateLimited = null, UpdatedAt = now });
        await TellResetAsync(account, now);
        var input = hold.Held ?? (hold.Stopped ? RateLimitTexts.Continuation : null);
        _logger.LogInformation("Project {ProjectId}: its account's rate limit has reset; {What}", id,
            hold.Held != null ? "what was held for it is sent" : hold.Stopped ? "it is told to carry on" : "what is held for it goes when it can take it");

        if (hold.Held != null)
        {
            // The user's own words, held: sent as the user's reply would have been
            await ReplyAndResumeAsync(id, hold.Held);
            return;
        }
        if (_lifecycle.IsRunning(project))
        {
            if (input != null && !await _lifecycle.TrySendHeldAsync(project, input))
                _logger.LogInformation("Project {ProjectId} cannot take the continuation now ({State}); it is not sent", id, project.Status.State);
            await NotifyStatusChanged(project);
            return;
        }
        // Its claude has exited since: resumed with the continuation, and what was held for it
        var reply = await WithTrackedLockAsync(project, () => ReplyAndResumeLockedAsync(project, input, onlyIfInterrupted: false, answersPending: false));
        if (!reply.Delivered) await NotifyStatusChanged(project);
        if (reply.SessionStart is { } sessionStart) await sessionStart;
    }

    /// <summary>The account's reset, told once by the session that told its hit (<see cref="ProjectStatus.AccountNotice"/>).</summary>
    private async Task TellResetAsync(string account, DateTime now)
    {
        if (_lifecycle.AccountLimits.TakeReset(account, now) is not { } limit
            || !_projects.TryGetValue(limit.AnnouncerId, out var announcer)) return;
        await _lifecycle.UpdateStatusAsync(announcer, status => status with
        {
            AccountNotice = new AccountNotice(now, RateLimitTexts.Reset(status.ProfileName, limit.Window)),
        });
        await NotifyStatusChanged(announcer);
    }

    /// <summary>The limit that holds what is sent to the session now: its own hold, else its account's; null when none does.</summary>
    private (DateTime Until, string? Window)? LimitHolding(ProjectInfo project) =>
        project.Status.RateLimited is { } hold ? (hold.Until, hold.Window)
        : _lifecycle.AccountLimits.LimitOf(AccountOf(project), DateTime.UtcNow) is { } limit ? (limit.Until, limit.Window)
        : null;

    /// <summary>
    /// Holds the session for its account's limit, when one holds it (<see cref="LimitHolding"/>): it is sent on at the
    /// reset, with <paramref name="userText"/>, the user's input, after what the user sent before it. False, with nothing
    /// held, when no limit holds it.
    /// </summary>
    private async Task<bool> HoldForLimitAsync(ProjectInfo project, string? userText)
    {
        if (LimitHolding(project) is not { Until: var until, Window: var window }) return false;
        var now = DateTime.UtcNow;
        await _lifecycle.UpdateStatusAsync(project, status => status with
        {
            RateLimited = status.RateLimited is { } hold
                ? hold with { Held = WithHeld(hold.Held, userText) }
                : new RateLimitHold(until, window, now, Held: userText),
            UpdatedAt = now,
        });
        _logger.LogInformation("Project {ProjectId}: its account is rate-limited until {Until:u}; {What} is held for the reset",
            project.Status.Id, until, userText != null ? "the user's input" : "what was sent to it");
        await NotifyStatusChanged(project);
        return true;
    }

    /// <summary>
    /// The account of the session: the config dir its last launch ran claude under, else the one its launch would, from
    /// its profile's and root's environment as they are now (claude's default when they cannot be read).
    /// </summary>
    private string AccountOf(ProjectInfo project)
    {
        if (project.ConfigDir is { } launched) return launched;
        var profileName = project.ProfileName ?? project.Status.ProfileName;
        try
        {
            var (action, stripEnvVarProfile, _) = ResolveLaunchAction(project, profileName);
            return ConfigDirOf(MergeAndExpandEnvironment(ProfileEnvironment(_snapshot, profileName ?? ""), action.Environment, profileName,
                stripEnvVarProfile) ?? []);
        }
        catch (LaunchConfigException)
        {
            return ConfigDirOf(new Dictionary<string, string>());
        }
    }

    /// <summary>After a start's recovery: each held session's limit marks its account again, and its resume is scheduled.</summary>
    private void ScheduleRecoveredLimits()
    {
        // The session that told of a limit tells of its reset, as before the restart
        foreach (var project in _projects.Values.OrderByDescending(p => p.Status.AccountNotice != null))
        {
            if (project.Status.RateLimited is not { } hold) continue;
            _lifecycle.AccountLimits.Seed(AccountOf(project), hold.Until, hold.Window, project.Status.Id);
            ScheduleLimitResume(project);
        }
    }
}
