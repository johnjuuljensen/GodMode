import type { RateLimitHold } from '../../signalr/types';
import { resetTime } from './resetTime';
import './RateLimit.css';

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
