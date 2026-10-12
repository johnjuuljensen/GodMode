import { useMemo } from 'react';
import { useAppStore, projectKey, openSessionKey, type ServerAttentionItem } from '../../store';

/**
 * What the inbox lists (#549): what needs the user, but the item of the session this window's main view shows
 * (`openSessionKey`), which is on screen already. Hidden here only, so other devices and voice still have it, and it
 * comes back when the view moves on, unless it was answered or marked seen meanwhile.
 */
export function useInboxAttention(): ServerAttentionItem[] {
  const attention = useAppStore(s => s.attention);
  const open = useAppStore(openSessionKey);
  return useMemo(
    () => open === null ? attention : attention.filter(i => projectKey(i.serverId, i.ProjectId) !== open),
    [attention, open],
  );
}
