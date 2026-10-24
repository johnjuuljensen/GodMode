import type { UsageWindow } from '../../signalr/types';

/** A window as the bar shows it: used, 0 to 1, and when it resets; 0 and reset once its reset has passed. */
export function windowUse(window: UsageWindow | null | undefined, now: number): { used: number; reset: boolean } | null {
  if (!window) return null;
  const reset = !!window.ResetsAt && new Date(window.ResetsAt).getTime() <= now;
  return { used: reset ? 0 : Math.min(Math.max(window.Utilization, 0), 1), reset };
}
