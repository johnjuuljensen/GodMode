import type { BackgroundTask } from '../../signalr/types';
import { backgroundName, backgroundTitle } from './backgroundState';
import './BackgroundTasks.css';

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
