import type { WorkProgress } from '../../signalr/types';
import './Progress.css';

/**
 * How far a session's work has got (#545), as its root's status script reported it: `x/N`, an epic's sub-issues
 * resolved out of all of them. With `bar`, as the project view's header has it, a thin bar under the count. None without one.
 */
export function ProgressMark({ progress, bar = false }: { progress?: WorkProgress | null; bar?: boolean }) {
  if (!progress) return null;
  const { Resolved, Total } = progress;
  const said = `${Resolved} of ${Total} resolved`;
  return (
    <span className={`progress-mark ${bar ? 'with-bar' : ''}`} title={said} aria-label={said} role="img">
      <span className="progress-count" aria-hidden="true">{Resolved}/{Total}</span>
      {bar && (
        <span className="progress-bar" aria-hidden="true">
          <span className="progress-bar-fill" style={{ width: `${Total > 0 ? (100 * Resolved) / Total : 0}%` }} />
        </span>
      )}
    </span>
  );
}
