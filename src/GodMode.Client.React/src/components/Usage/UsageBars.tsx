import { useEffect, useState } from 'react';
import type { AccountUsage, UsageCeiling, UsageWindow } from '../../signalr/types';
import { useAppStore } from '../../store';
import { isListed, profileNameOf, sameProfile } from '../../store/hierarchy';
import { resetTime } from '../RateLimit/resetTime';
import { windowUse } from './windowUse';
import './Usage.css';

/** How often the bars look again at whether a window has reset. */
const RESET_TICK_MS = 60_000;

const percent = (fraction: number) => `${Math.round(fraction * 100)}%`;

function Bar({ label, window, ceiling, now }: { label: string; window?: UsageWindow | null; ceiling?: number | null; now: number }) {
  const use = windowUse(window, now);
  if (!use) return null;
  const over = ceiling != null && use.used >= ceiling;
  const level = over ? 'over' : use.used >= 0.8 ? 'high' : 'ok';
  return (
    <span className={`usage-bar usage-bar-${level}`} role="meter" aria-label={`${label} usage`}
      aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(use.used * 100)}>
      <span className="usage-bar-fill" style={{ width: percent(use.used) }} />
      {ceiling != null && <span className="usage-bar-ceiling" style={{ left: percent(Math.min(ceiling, 1)) }} />}
    </span>
  );
}

/** One window's line of the tooltip: "Five-hour: 27%, resets 15:50", or "reset" once it has. */
function line(label: string, window: UsageWindow | null | undefined, ceiling: number | null | undefined, now: number): string | null {
  const use = windowUse(window, now);
  if (!use) return null;
  const when = use.reset ? 'reset' : window?.ResetsAt ? `resets ${resetTime(window.ResetsAt, new Date(now))}` : null;
  const limit = ceiling != null ? `, ceiling ${percent(ceiling)}${use.used >= ceiling ? ': holds new work' : ''}` : '';
  return `${label}: ${percent(use.used)}${when ? `, ${when}` : ''}${limit}`;
}

/**
 * An account's five-hour and weekly usage (#600), as two thin bars, each with its ceiling's mark: as of the account's last
 * turn, and a window whose reset has passed is empty. Nothing when the account has no reading.
 */
export function UsageBars({ usage, ceiling }: { usage?: AccountUsage; ceiling?: UsageCeiling | null }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), RESET_TICK_MS);
    return () => clearInterval(timer);
  }, []);
  if (!usage || (!usage.FiveHour && !usage.SevenDay)) return null;
  const title = [
    line('Five-hour', usage.FiveHour, ceiling?.FiveHour, now),
    line('Weekly', usage.SevenDay, ceiling?.SevenDay, now),
    `${usage.Name}, as of ${resetTime(usage.ObservedAt, new Date(now))}`,
  ].filter(Boolean).join('\n');
  return (
    <span className="usage-bars" title={title}>
      <Bar label="five-hour" window={usage.FiveHour} ceiling={ceiling?.FiveHour} now={now} />
      <Bar label="weekly" window={usage.SevenDay} ceiling={ceiling?.SevenDay} now={now} />
    </span>
  );
}

/** A root's bars: its account's reading on its server, with its ceiling. */
export function RootUsage({ serverId, profileName, rootName }: { serverId: string; profileName: string; rootName: string }) {
  const conn = useAppStore(s => s.serverConnections.find(c => c.serverInfo.Id === serverId));
  const root = conn?.roots.find(r => r.Name === rootName && sameProfile(profileNameOf(r.ProfileName), profileName));
  if (!conn || !root?.Account) return null;
  return <UsageBars usage={conn.usage.find(u => u.Account === root.Account)} ceiling={root.UsageCeiling} />;
}

/**
 * A profile's bars: its account's reading on each server that lists it, once per account. A profile is a name across
 * servers, and each server's account is its own.
 */
export function ProfileUsage({ profileName }: { profileName: string }) {
  const connections = useAppStore(s => s.serverConnections);
  const shown = connections.filter(isListed).flatMap(conn => {
    const profile = conn.profiles.find(p => sameProfile(p.Name, profileName));
    const usage = profile?.Account ? conn.usage.find(u => u.Account === profile.Account) : undefined;
    return profile && usage ? [{ key: `${conn.serverInfo.Id}:${usage.Account}`, usage, ceiling: profile.UsageCeiling }] : [];
  });
  return <>{shown.map(s => <UsageBars key={s.key} usage={s.usage} ceiling={s.ceiling} />)}</>;
}
