using GodMode.Server.Models;
using GodMode.Shared.Enums;
using GodMode.Shared.Models;

namespace GodMode.Server.Services;

/// <summary>
/// What sessions send one another through the server: a child's <c>message_parent</c>, the server's notices to a parent
/// with the fleet's tools when its child ends a turn or waits on the user, and the fleet's <c>send</c> to a session
/// waiting on the user. Nothing of it answers a permission prompt or a question, or interrupts a turn: it is held until
/// the receiver can take input (<see cref="ProjectLifecycle.CanTakeInput"/>), then delivered as one message. Messages are
/// held on disk, out of every working folder (<see cref="SessionInbox"/>), notices in memory, and a receiver whose claude
/// is not running gets its messages with its next resume or reply, and its notices dropped. A session's parent, for all
/// of it, is the one the server recorded at its create (<see cref="FleetGrantFile.Grant.Parent"/>), never its
/// <c>status.json</c>'s, which the session can write; and at delivery each sender is checked again, so what no longer
/// holds (a removed link, revoked tools, a re-parented sender) is dropped, not delivered.
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
        if (ServerParentOf(sender) is not { } parentId)
            throw new InvalidOperationException("This session has no parent session to message: the server has no record of a session that started it.");
        if (!_projects.TryGetValue(parentId, out var parent))
            throw new InvalidOperationException($"This session's parent, {parentId}, is no longer on this server: the message was not sent.");
        if (!ParentLinkAllowed(parentId, projectId, out var missing))
            throw new InvalidOperationException($"This session's parent, {parentId}, is in another root, and {missing}: the message was not sent.");

        _logger.LogInformation("Project {ProjectId} messages its parent {ParentId} ({Length} characters)", projectId, parentId, text.Length);
        await HoldAsync(parent, new SessionInbox.HeldMessage(DateTime.UtcNow, projectId, SessionInbox.HeldKind.Message, text));
        return await DeliverHeldAsync(parent);
    }

    public async Task<Delivery> SendOrHoldAsync(string projectId, string text, string? senderId)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");
        // Refused before it is held, as the app's reply is: a failed create would never take it
        RefuseFailedCreate(project);
        CheckText(text);
        SlashCommands.Check(text, project.Status);
        var held = new SessionInbox.HeldMessage(DateTime.UtcNow, senderId, SessionInbox.HeldKind.Send, text);

        if (WaitsOnTheUser(project) is { } waiting)
        {
            await HoldAsync(project, held);
            // The user may have answered since the check: then it takes it now
            return await DeliverHeldAsync(project) is { Delivered: true } delivered ? delivered : new Delivery(false, waiting);
        }
        // Messages held before it go first: it does not overtake them
        if (SessionInbox.Any(project.RootPath, project.SessionId))
        {
            await HoldAsync(project, held);
            if (_lifecycle.IsRunning(project)) return await DeliverHeldAsync(project);
            await ResumeProjectAsync(projectId);
            return new Delivery(true);
        }
        try
        {
            await ReplyAndResumeAsync(projectId, text, answersPending: false);
            return new Delivery(true);
        }
        catch (InvalidOperationException) when (WaitsOnTheUser(project) is { } waitingNow)
        {
            // A prompt came between the check and the send, which refused the text: held, as above
            await HoldAsync(project, held);
            return new Delivery(false, waitingNow);
        }
    }

    private static void CheckText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("The message is empty.");
        if (text.Length > SessionInbox.MaxTextLength)
            throw new ArgumentException($"The message has {text.Length} characters; at most {SessionInbox.MaxTextLength} are sent. Shorten it, or point to where the rest is.");
    }

    /// <summary>
    /// Why the session waits on the user, whose answer a message must not be taken for: a permission prompt or a question
    /// its claude waits on, a question it ended its turn on, or one it was stopped while asking (a restart keeps it). Null
    /// when it waits on nothing of the user's.
    /// </summary>
    private static string? WaitsOnTheUser(ProjectInfo project) =>
        project.Process.OldestPending is { } pending ? WaitingOn(pending.Question != null)
        : project.Status.State == ProjectState.WaitingInput || project.Status.CurrentQuestion != null ? WaitingOn(question: true)
        : null;

    private static string WaitingOn(bool question) =>
        $"it waits on the user's answer to {(question ? "a question" : "a permission prompt")}, and gets it once the user has answered and its turn has ended";

    /// <summary>Why the session cannot take what is held now; null when it can.</summary>
    private string? WhyHeld(ProjectInfo project) =>
        _lifecycle.CanTakeInput(project) ? null
        : WaitsOnTheUser(project) is { } waiting ? waiting
        : !_lifecycle.IsRunning(project) || project.Process.Stopping
            ? project.Process.Launching ? "it is starting, and gets it once it has started" : "it is stopped, and gets it when it is resumed"
        : project.Status.State == ProjectState.Error ? "it is in error, waiting on the user, and gets it after its next turn"
        : "it is working on a turn, and gets it when the turn ends";

    private async Task HoldAsync(ProjectInfo project, SessionInbox.HeldMessage message)
    {
        await project.Process.InboxLock.WaitAsync();
        try { SessionInbox.Append(project.RootPath, project.SessionId, message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The message could not be kept for {project.Status.Id}: {ex.Message}", ex);
        }
        finally { project.Process.InboxLock.Release(); }
    }

    /// <summary>
    /// The held messages that still hold, rendered with their labels, and how many were read (delivered or dropped).
    /// A child's message holds while its recorded parent is still the receiver, in its root or through a link; a
    /// session's <c>send</c> while the sender still has the fleet's tools and sees the receiver (its profile, or a link).
    /// One from a session the server no longer has cannot be checked, and is dropped too. What no longer holds is
    /// dropped, and logged.
    /// </summary>
    private (List<string> Rendered, int Read) HeldToDeliver(ProjectInfo receiver)
    {
        var held = SessionInbox.Read(receiver.RootPath, receiver.SessionId);
        var rendered = new List<string>();
        foreach (var message in held)
        {
            var sender = message.From is { } from && _projects.TryGetValue(from, out var tracked) ? tracked : null;
            if (!StillHolds(message, sender, receiver))
            {
                _logger.LogWarning("Project {ProjectId}: a held {Kind} from {From} is dropped: its sender may no longer message it",
                    receiver.Status.Id, message.Kind, message.From ?? "the server's credential");
                continue;
            }
            var label = sender != null ? SessionInbox.LabelOf(sender.Status.Id, sender.Status.Name) : SessionInbox.OverseerLabel;
            rendered.Add(SessionInbox.Render(label, message.Text));
        }
        return (rendered, held.Count);
    }

    private bool StillHolds(SessionInbox.HeldMessage message, ProjectInfo? sender, ProjectInfo receiver) => message switch
    {
        { From: null, Kind: SessionInbox.HeldKind.Send } => true,
        { Kind: SessionInbox.HeldKind.Message } when sender != null =>
            ServerParentOf(sender) == receiver.Status.Id && ParentLinkAllowed(receiver.Status.Id, sender.Status.Id, out _),
        { Kind: SessionInbox.HeldKind.Send } when sender != null =>
            HasFleetTools(sender) && SeesFrom(sender.Status.Id, receiver.Status.Id),
        _ => false,
    };

    /// <summary>Whether a session's fleet tools see <paramref name="toId"/>: its own profile, or a link from its root.</summary>
    private bool SeesFrom(string fromId, string toId) =>
        RootRef.OfId(fromId) is { } from && RootRef.OfId(toId) is { } to && (from.Profile == to.Profile || _links.Linked(from, to));

    /// <summary>The notices held for the receiver that still hold: it has the fleet's tools, and each child is still its own.</summary>
    private KeyValuePair<string, HeldNotice>[] NoticesToDeliver(ProjectInfo receiver)
    {
        var notices = receiver.Process.HeldNotices.ToArray().OrderBy(n => n.Value.At).ToArray();
        var hasTools = notices.Length > 0 && HasFleetTools(receiver);
        foreach (var gone in notices.Where(n => !hasTools || !_projects.TryGetValue(n.Key, out var child)
            || ServerParentOf(child) != receiver.Status.Id || !ParentLinkAllowed(receiver.Status.Id, n.Key, out _)))
            receiver.Process.HeldNotices.TryRemove(gone);
        return notices.Where(n => receiver.Process.HeldNotices.ContainsKey(n.Key)).ToArray();
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

                var (messages, read) = HeldToDeliver(project);
                var notices = NoticesToDeliver(project);
                if (SessionInbox.Compose(messages, notices.Select(n => n.Value.Text)) is { } input
                    && !await _lifecycle.TrySendHeldAsync(project, input))
                    return new Delivery(false, WhyHeld(project) ?? "it took no input");

                SessionInbox.RemoveFirst(project.RootPath, project.SessionId, read);
                foreach (var notice in notices) project.Process.HeldNotices.TryRemove(notice);
                if (messages.Count + notices.Length > 0)
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
    /// resumed with, and how many were read, to take off once sent (<see cref="TakeHeldMessagesAsync"/>). Its notices
    /// are dropped.
    /// </summary>
    private async Task<(string? Text, int Count)> PeekHeldMessagesAsync(ProjectInfo project)
    {
        await project.Process.InboxLock.WaitAsync();
        try
        {
            project.Process.HeldNotices.Clear();
            var (messages, read) = HeldToDeliver(project);
            return (SessionInbox.Compose(messages, []), read);
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
        try { SessionInbox.RemoveFirst(project.RootPath, project.SessionId, count); }
        finally { project.Process.InboxLock.Release(); }
        _logger.LogInformation("Project {ProjectId}: dealt with {Messages} held message(s) with its resume", project.Status.Id, count);
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
        if (_lifecycle.CanTakeInput(project) && (!project.Process.HeldNotices.IsEmpty || SessionInbox.Any(project.RootPath, project.SessionId)))
            DeliverInBackground(project);
    }

    private void DeliverInBackground(ProjectInfo project) => _ = Task.Run(async () =>
    {
        try { await DeliverHeldAsync(project); }
        catch (Exception ex) { _logger.LogError(ex, "Could not deliver what is held for project {ProjectId}", project.Status.Id); }
    });

    /// <summary>
    /// Tells the session's recorded parent, in one line, when the session ends a turn, waits on the user, fails or
    /// stops: only a parent with the fleet's tools now (<see cref="HasFleetTools(ProjectInfo)"/>), whose claude runs, in
    /// the session's root or one a link lets it oversee. A parent in the session's <c>CLAUDE_CONFIG_DIR</c> hears of the
    /// turn's end through <c>notify_when_idle</c>, so it gets no notice of Idle, and gets the rest. Two sessions are in
    /// the same dir when their last launches on this server were, or when neither has launched since the server started.
    /// A notice held for the parent about this session is replaced by the newer one.
    /// </summary>
    private void NoticeParent(ProjectInfo child)
    {
        var status = child.Status;
        if (!child.Process.NoticeState(status.State)) return;
        if (status.State is not (ProjectState.Idle or ProjectState.WaitingInput or ProjectState.WaitingPermission or ProjectState.Error or ProjectState.Stopped))
            return;
        if (ServerParentOf(child) is not { } parentId || !_projects.TryGetValue(parentId, out var parent)) return;
        if (!_lifecycle.IsRunning(parent) || !HasFleetTools(parent) || !ParentLinkAllowed(parentId, status.Id, out _)) return;
        if (status.State == ProjectState.Idle && SameConfigDir(parent, child)) return;

        parent.Process.HeldNotices[status.Id] = new HeldNotice(NoticeOf(status), DateTime.UtcNow);
        _logger.LogInformation("Project {ParentId} is told its child {ProjectId} is {State}", parentId, status.Id, status.State);
        DeliverInBackground(parent);
    }

    private static bool SameConfigDir(ProjectInfo a, ProjectInfo b) =>
        (a.ConfigDir, b.ConfigDir) switch
        {
            (null, null) => true,
            ({ } x, { } y) => PathComparer.Equals(x, y),
            _ => false,
        };

    /// <summary>
    /// Whether the session <paramref name="parentId"/> may hear from <paramref name="childId"/>: the same root, or a
    /// <see cref="FleetLinks"/> entry from the parent's root to the child's; <paramref name="missing"/> says which link is
    /// missing when not.
    /// </summary>
    private bool ParentLinkAllowed(string parentId, string childId, out string missing)
    {
        missing = "";
        if (RootRef.OfId(parentId) is not { } from || RootRef.OfId(childId) is not { } to) return false;
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
