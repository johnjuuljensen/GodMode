namespace GodMode.Shared.Models;

/// <summary>
/// A task the session's claude runs in the background (issue #432): a subagent, a shell or a monitor, or a workflow, which
/// runs on while the session is idle between turns. As claude's last <c>system/background_tasks_changed</c> listed it.
/// </summary>
/// <param name="Id">claude's <c>task_id</c>.</param>
/// <param name="Type">claude's <c>task_type</c>: <c>local_agent</c> (a subagent), <c>local_bash</c> (a shell or a monitor), <c>local_workflow</c>, or another claude adds.</param>
/// <param name="Description">What the task does, as claude describes it.</param>
/// <param name="Step">What the task is doing now: the <c>description</c> of its last <c>system/task_progress</c> (a subagent's or a workflow's). Null until it reports one.</param>
public record BackgroundTask(string Id, string Type, string Description, string? Step = null);
