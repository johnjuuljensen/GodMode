import type { Importance } from '../../signalr/types';
import { IMPORTANCE_HINTS, IMPORTANCE_LABELS, IMPORTANCE_ORDER } from './importanceTiers';
import { useAppStore } from '../../store';
import './Importance.css';

/** A session's mark: a star when it is important, "quiet" when it is quiet, nothing when normal. */
export function ImportanceMark({ importance }: { importance?: Importance | null }) {
  if (importance === 'Important') return <span className="importance-mark important" title="Important: it interrupts" aria-label="important">★</span>;
  if (importance === 'Quiet') return <span className="importance-mark quiet" title="Quiet: the inbox alone" aria-label="quiet">quiet</span>;
  return null;
}

/** The tier as three choices (the project header), the current one pressed. */
export function ImportancePicker({ serverId, projectId, importance }: { serverId: string; projectId: string; importance?: Importance | null }) {
  const setImportance = useAppStore(s => s.setImportance);
  const current = importance ?? 'Normal';
  return (
    <div className="importance-picker" role="group" aria-label="How much it may interrupt">
      {IMPORTANCE_ORDER.map(tier => (
        <button
          key={tier}
          className={`importance-choice ${tier.toLowerCase()} ${tier === current ? 'active' : ''}`}
          aria-pressed={tier === current}
          title={`${IMPORTANCE_LABELS[tier]}: ${IMPORTANCE_HINTS[tier]}`}
          onClick={() => { if (tier !== current) void setImportance(serverId, projectId, tier).catch(err => console.error('Failed to set the importance:', err)); }}
        >
          {tier === 'Important' ? '★' : IMPORTANCE_LABELS[tier]}
        </button>
      ))}
    </div>
  );
}
