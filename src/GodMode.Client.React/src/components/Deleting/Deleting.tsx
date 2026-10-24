import { projectKey, useAppStore } from '../../store';
import './Deleting.css';

interface Props {
  serverId: string;
  projectId: string;
  /** How many of the delete script's last lines it shows under "Deleting…". */
  lines?: number;
}

/**
 * A session being deleted (#599): "Deleting…" with a spinner, and the delete script's last lines (its
 * CreationProgress) under it, from the server's word that the delete started until the session leaves the list.
 */
export function DeletingNote({ serverId, projectId, lines = 1 }: Props) {
  const progress = useAppStore(s => s.deleteProgress[projectKey(serverId, projectId)]);
  const shown = progress?.slice(-lines) ?? [];
  return (
    <div className="deleting-note" role="status">
      <span className="deleting-label"><span className="deleting-spinner" aria-hidden="true" />Deleting…</span>
      {shown.map((line, i) => <span key={i} className="deleting-line" title={line}>{line}</span>)}
    </div>
  );
}
