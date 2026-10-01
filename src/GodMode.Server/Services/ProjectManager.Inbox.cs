using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// What sessions send one another through the server: a child's <c>message_parent</c>, the server's notices to a parent
/// with the fleet's tools when its child ends a turn or waits on the user, and the fleet's <c>send</c> to a session
/// waiting on the user. Nothing of it answers a permission prompt or a question, or interrupts a turn: it is held until
/// the receiver can take input (<see cref="ProjectLifecycle.CanTakeInput"/>), then delivered as one message. Messages are
/// held on disk (<see cref="SessionInbox"/>), notices in memory, and a receiver whose claude is not running gets its
/// messages with its next resume or reply, and its notices dropped.
/// </summary>
public partial class ProjectManager
{
    /// <summary>A notice's start: from the server, not the user or a session.</summary>
    public const string NoticePrefix = "[GodMode notice]";

    /// <summary>The most characters a notice quotes of what a session waits on.</summary>
    private const int NoticeDetailLength = 200;

    public async Task<Delivery> MessageParentAsync(string projectId, string text)
    {
        if (!_projects.TryGetValue(projectId, out var sender))
            throw new KeyNotFoundException($"Project {projectId} not found");
        CheckText(text);
        if (sender.Status.ParentId is not { } parentId)
            throw new InvalidOperationException("This session has no parent session to message: it was started on its own, not by another session.");
        if (!_projects.TryGetValue(parentId, out var parent))
            throw new InvalidOperationException($"This session's parent, {parentId}, is no longer on this server: the message was not sent.");
        if (!ParentLinkAllowed(parent, sender, out var missing))
            throw new InvalidOperationException($"This session's parent, {parentId}, is in another root, and {missing}: the message was not sent.");

        _logger.LogInformation("Project {ProjectId} messages its parent {ParentId} ({Length} characters)", projectId, parentId, text.Length);
        await HoldAsync(parent, SessionInbox.LabelOf(projectId, sender.Status.Name), text);
        return await DeliverHeldAsync(parent);
    }

    public async Task<Delivery> SendOrHoldAsync(string projectId, string text, string? senderId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");
        CheckText(text);
        var label = senderId != null && _projects.TryGetValue(senderId, out var sender)
            ? SessionInbox.LabelOf(senderId, sender.Status.Name)
            : SessionInbox.OverseerLabel;

        if (WaitsOnTheUser(project) is { } waiting)
        {
            await HoldAsync(project, label, text);
            // The user may have answered since the check: then it takes it now
            return await DeliverHeldAsync(project) is { Delivered: true } delivered ? delivered : new Delivery(false, waiting);
        }
        try
        {
            await ReplyAndResumeAsync(projectId, text, answersPending: false);
            return new Delivery(true);
        }
        catch (InvalidOperationException) when (WaitsOnTheUser(project) is { } waitingNow)
        {
            // A prompt came between the check and the send, which refused the text: held, as above
            await HoldAsync(project, label, text);
            return new Delivery(false, waitingNow);
        }
    }

    private static void CheckText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("The message is empty.");
        if (text.Length > SessionInbox.MaxTextLength)
            throw new ArgumentException($"The message has {text.Length} characters; at most {SessionInbox.MaxTextLength} are sent. Shorten it, or point to where the rest is.");
    }

    /// <summary>Why the session's running claude waits on the user (a permission prompt, a question); null when it does not, or does not run.</summary>
    private string? WaitsOnTheUser(ProjectInfo project) =>
        !_lifecycle.IsRunning(project) ? null
        : project.Process.OldestPending is { } pending ? WaitingOn(pending.Question != null)
        : project.Status.State == ProjectState.WaitingInput ? WaitingOn(question: true)
        : null;

    private static string WaitingOn(bool question) =>
        $"it waits on the user's answer to {(question ? "a question" : "a permission prompt")}, and gets it once the user has answered and its turn has ended";

    /// <summary>Why the session cannot take what is held now; null when it can.</summary>
    private string? WhyHeld(ProjectInfo project) =>
        _lifecycle.CanTakeInput(project) ? null
        : !_lifecycle.IsRunning(project) || project.Process.Stopping
            ? project.Process.Launching ? "it is starting, and gets it once it has started" : "it is stopped, and gets it when it is resumed"
        : WaitsOnTheUser(project)
            ?? (project.Status.State == ProjectState.Error ? "it is in error, waiting on the user, and gets it after its next turn" : "it is working on a turn, and gets it when the turn ends");

    private async Task HoldAsync(ProjectInfo project, string label, string text)
    {
        await project.Process.InboxLock.WaitAsync();
        try { SessionInbox.Append(project.StatePath, new SessionInbox.HeldMessage(DateTime.UtcNow, label, text)); }
        finally { project.Process.InboxLock.Release(); }
    }

    /// <summary>
    /// Delivers everything held for the session as one input, if it can take input now; otherwise says why it is
    /// held. Under its resume lock, so a stop or a launch comes before it or after it. A session whose claude does not
    /// run loses its notices: its messages wait for its next resume or reply (<see cref="ReplyAndResumeLockedAsync"/>).
    /// </summary>
    private async Task<Delivery> DeliverHeldAsync(ProjectInfo project)
    {
        if (!IsTracked(project)) return new Delivery(false, "it has left the list");
        return await WithResumeLockAsync(project, async () =>
        {
            await project.Process.InboxLock.WaitAsync();
            try
            {
                if (!_lifecycle.IsRunning(project)) project.Process.HeldNotices.Clear();
                if (WhyHeld(project) is { } why) return new Delivery(false, why);

                var messages = SessionInbox.Read(project.StatePath);
                var notices = project.Process.HeldNotices.ToArray().OrderBy(n => n.Value.At).ToArray();
                if (SessionInbox.Compose(messages, notices.Select(n => n.Value.Text)) is not { } input) return new Delivery(true);
                if (!await _lifecycle.TrySendHeldAsync(project, input)) return new Delivery(false, WhyHeld(project) ?? "it took no input");

                SessionInbox.RemoveFirst(project.StatePath, messages.Count);
                foreach (var notice in notices) project.Process.HeldNotices.TryRemove(notice);
                _logger.LogInformation("Project {ProjectId}: delivered {Messages} held message(s) and {Notices} notice(s)",
                    project.Status.Id, messages.Count, notices.Length);
                return new Delivery(true);
            }
            finally
            {
                project.Process.InboxLock.Release();
            }
        });
    }

    /// <summary>
    /// The messages held for a session about to be resumed, under its resume lock: their text, to send with what it is
    /// resumed with, and how many, to take off once sent (<see cref="TakeHeldMessagesAsync"/>). Its notices are dropped.
    /// </summary>
    private async Task<(string? Text, int Count)> PeekHeldMessagesAsync(ProjectInfo project)
    {
        await project.Process.InboxLock.WaitAsync();
        try
        {
            project.Process.HeldNotices.Clear();
            var messages = SessionInbox.Read(project.StatePath);
            return (SessionInbox.Compose(messages, []), messages.Count);
        }
        finally
        {
            project.Process.InboxLock.Release();
        }
    }

    private async Task TakeHeldMessagesAsync(ProjectInfo project, int count)
    {
        if (count == 0) return;
        await project.Process.InboxLock.WaitAsync();
        try { SessionInbox.RemoveFirst(project.StatePath, count); }
        finally { project.Process.InboxLock.Release(); }
        _logger.LogInformation("Project {ProjectId}: delivered {Messages} held message(s) with its resume", project.Status.Id, count);
    }

    /// <summary>A reply, with what is held for the session after it: the user's words first, as they wrote them.</summary>
    private static string? WithHeld(string? text, string? held) =>
        (text, held) switch
        {
            (null, _) => held,
            (_, null) => text,
            _ => $"{text}\n\n{held}",
        };

    /// <summary>
    /// After every status push: the session's parent hears of a change it should (<see cref="NoticeParent"/>), and
    /// what is held for the session is delivered if it can take it now. Neither is waited for: a delivery waits for
    /// the receiver's resume lock, which whoever pushed this may hold.
    /// </summary>
    private void DeliverOnStatusChange(ProjectInfo project)
    {
        NoticeParent(project);
        if (!_lifecycle.IsRunning(project)) project.Process.HeldNotices.Clear();
        if (_lifecycle.CanTakeInput(project) && (!project.Process.HeldNotices.IsEmpty || SessionInbox.Any(project.StatePath)))
            DeliverInBackground(project);
    }

    private void DeliverInBackground(ProjectInfo project) => _ = Task.Run(async () =>
    {
        try { await DeliverHeldAsync(project); }
        catch (Exception ex) { _logger.LogError(ex, "Could not deliver what is held for project {ProjectId}", project.Status.Id); }
    });

    /// <summary>
    /// Tells the session's parent, in one line, when the session ends a turn, waits on the user, fails or stops: only a
    /// parent with the fleet's tools now (<see cref="HasFleetTools(ProjectInfo)"/>), whose claude runs, in the session's
    /// root or one a link lets it oversee, and under another <c>CLAUDE_CONFIG_DIR</c> than the session's last launch: in
    /// the same one it subscribes with <c>notify_when_idle</c>. A notice held for the parent about this session is
    /// replaced by the newer one.
    /// </summary>
    private void NoticeParent(ProjectInfo child)
    {
        var status = child.Status;
        if (!child.Process.NoticeState(status.State)) return;
        if (status.State is not (ProjectState.Idle or ProjectState.WaitingInput or ProjectState.WaitingPermission or ProjectState.Error or ProjectState.Stopped))
            return;
        if (status.ParentId is not { } parentId || !_projects.TryGetValue(parentId, out var parent)) return;
        if (!_lifecycle.IsRunning(parent) || !HasFleetTools(parent) || !ParentLinkAllowed(parent, child, out _)) return;
        // In one config dir the parent hears of it through Claude Code's own channel (notify_when_idle): a notice
        // would wake it twice
        if (parent.ConfigDir != null && child.ConfigDir != null && PathComparer.Equals(parent.ConfigDir, child.ConfigDir)) return;

        parent.Process.HeldNotices[status.Id] = new HeldNotice(NoticeOf(status), DateTime.UtcNow);
        _logger.LogInformation("Project {ParentId} is told its child {ProjectId} is {State}", parentId, status.Id, status.State);
        DeliverInBackground(parent);
    }

    /// <summary>
    /// Whether <paramref name="parent"/> may hear from <paramref name="child"/>: the same root, or a <see cref="FleetLinks"/>
    /// entry from the parent's root to the child's; <paramref name="missing"/> says which link is missing when not.
    /// </summary>
    private bool ParentLinkAllowed(ProjectInfo parent, ProjectInfo child, out string missing)
    {
        missing = "";
        if (RootRef.OfId(parent.Status.Id) is not { } from || RootRef.OfId(child.Status.Id) is not { } to) return false;
        if (_links.AllowsParent(from, to)) return true;
        missing = FleetLinks.Missing(from, to);
        return false;
    }

    /// <summary>The notice of a child's state: its ID and name, its state, what it waits on, and its pull request.</summary>
    public static string NoticeOf(ProjectStatus status)
    {
        var waits = status switch
        {
            { PendingPermission: { } permission } => $", waiting on the user's permission for {permission.ToolName}: {permission.Summary}",
            { PendingQuestion: { } question } => $", waiting on the user's answer to: {string.Join(" ", question.Questions.Select(q => q.Question))}",
            { State: ProjectState.WaitingInput, CurrentQuestion: { } asked } => $", waiting on the user's answer to: {asked}",
            { State: ProjectState.Error } => $": {status.LastError ?? "it failed"}",
            { State: ProjectState.Idle } => ": its turn ended",
            _ => "",
        };
        var pullRequest = status.PullRequest is { Url: { Length: > 0 } url } ? $"; pull request {url}" : "";
        return SessionInbox.OneLine($"{NoticePrefix} Session {status.Id} \"{SessionInbox.OneLine(status.Name, 100)}\" is {status.State}" +
            $"{SessionInbox.OneLine(waits, NoticeDetailLength)}{pullRequest}.", 600);
    }
}

/// <summary>What became of a message: delivered to the receiver's claude now, or held, and why.</summary>
public sealed record Delivery(bool Delivered, string? Held = null);
