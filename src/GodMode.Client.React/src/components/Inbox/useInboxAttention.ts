import { useMemo } from 'react';
import { useAppStore, projectKey, openSessionKey, type ServerAttentionItem } from '../../store';

/**
 * What needs the user, but the item of the session this window's main view shows (#549, `openSessionKey`), which is
 * on screen already. Hidden here only, so other devices and voice still have it, and it comes back when the view moves
 * on, unless opening the project settled it (#592: `useMarkOpened`), or it was answered or marked seen meanwhile.
 */
export function useAttentionBesidesOpen(): ServerAttentionItem[] {
  const attention = useAppStore(s => s.attention);
  const open = useAppStore(openSessionKey);
  return useMemo(
    () => open === null ? attention : attention.filter(i => projectKey(i.serverId, i.ProjectId) !== open),
    [attention, open],
  );
}

/**
 * What the inbox lists: `useAttentionBesidesOpen`, but a permission prompt or an AskUserQuestion the user has looked at
 * (#592: `Acknowledged`), which waits on its answer in its project's view, and leaves the inbox for good. The left list
 * still marks its project as waiting.
 */
export function useInboxAttention(): ServerAttentionItem[] {
  const attention = useAttentionBesidesOpen();
  return useMemo(() => attention.filter(i => !i.Acknowledged), [attention]);
}
