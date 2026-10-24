import type { RateLimitHold } from '../../signalr/types';
import './RateLimit.css';

/** The reset as the row says it: "15:50" today, "Mon 15:50" on another day. */
export function resetTime(until: string, now: Date = new Date()): string {
  const at = new Date(until);
  const time = `${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;
  return at.toDateString() === now.toDateString() ? time : `${at.toLocaleDateString([], { weekday: 'short' })} ${time}`;
}

/**
 * A session its account's rate limit holds (#596): "limited until 15:50". The server sends it on then, with what the
 * user sent it meanwhile, which the title says is waiting. Nothing when no limit holds it.
 */
export function RateLimitMark({ hold }: { hold?: RateLimitHold | null }) {
  if (!hold) return null;
  const until = resetTime(hold.Until);
  const title = hold.Held
    ? `Its account's rate limit holds it; what you sent goes at ${until}`
    : `Its account's rate limit ${hold.Stopped ? 'stopped' : 'holds'} it; it carries on at ${until}`;
  return <span className="rate-limit-mark" title={title} aria-label={`limited until ${until}`}>limited until {until}</span>;
}
