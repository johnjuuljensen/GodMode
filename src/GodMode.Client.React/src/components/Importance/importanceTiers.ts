import type { Importance } from '../../signalr/types';

/**
 * How much a session may interrupt the user (#438), least first. The server keeps it with the session, so every
 * device and voice agree; a project without one is Normal.
 */
export const IMPORTANCE_ORDER: readonly Importance[] = ['Quiet', 'Normal', 'Important'];

export const IMPORTANCE_LABELS: Record<Importance, string> = {
  Quiet: 'Quiet',
  Normal: 'Normal',
  Important: 'Important',
};

export const IMPORTANCE_HINTS: Record<Importance, string> = {
  Quiet: 'Its results and errors stay in the inbox; what it asks still notifies',
  Normal: 'A notification and an announcement',
  Important: 'A sound, a heads-up on the phone, and said first',
};
