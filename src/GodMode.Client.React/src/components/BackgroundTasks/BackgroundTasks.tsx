import type { BackgroundTask, ProjectSummary } from '../../signalr/types';
import './BackgroundTasks.css';

/**
 * Whether the session is idle and working in the background all the same (#432): its turn has ended, and claude still
 * runs subagents, shells, monitors or workflows. The list and header show it so, not as Idle.
 */
export const inBackground = (p: Pick<ProjectSummary, 'State' | 'BackgroundTasks'>) =>
  p.State === 'Idle' && !!p.BackgroundTasks?.length;

/** What a session in the background is named as: its dot's and badge's label. */
export const backgroundName = (count: number) => `Working in the background (${count} ${count === 1 ? 'task' : 'tasks'})`;

/** The tasks as a tooltip lists them: each one's description, and its last step when it reported one. */
export function backgroundTitle(tasks: readonly BackgroundTask[]): string {
  return [backgroundName(tasks.length), ...tasks.map(t => `• ${t.Description || t.Type}${t.Step ? ` — ${t.Step}` : ''}`)].join('\n');
}

/** A session's background tasks as a small badge with their count, which lists them in its tooltip. None without any. */
export function BackgroundBadge({ tasks }: { tasks?: readonly BackgroundTask[] | null }) {
  if (!tasks?.length) return null;
  return (
    <span className="background-badge" role="img" aria-label={backgroundName(tasks.length)} title={backgroundTitle(tasks)}>
      <span className="background-badge-spinner" aria-hidden="true" />
      {tasks.length}
    </span>
  );
}
