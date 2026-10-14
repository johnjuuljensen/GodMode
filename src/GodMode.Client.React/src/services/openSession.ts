/**
 * The app told which session this window's main view shows (#549), so it holds back that session's notification while
 * the app is in front (Android's AttentionNotifier; Windows' toasts, #491, can ask the same). Over the bridge as voice
 * is: session.open, whenever the open session changes. And the server told the user opened it (#592), to settle its item.
 */
import { useEffect, useRef, useState } from 'react';
import * as bridge from './hostBridge';
import { useAppStore, openSessionKey, projectKey } from '../store';

/** Tells the app the session shown, or none. A shell without session.open (an older app) has nothing to hold back. */
export const reportOpenSession = (session: bridge.AttentionLinkPayload | null): Promise<void> =>
  bridge.request('session.open', { Session: session })
    .then(() => undefined, err => console.debug('[openSession] session.open failed:', err));

/** Keeps the app told which session this window's main view shows (`openSessionKey`). */
export function useReportOpenSession(): void {
  const open = useAppStore(openSessionKey);
  useEffect(() => {
    const sel = open && useAppStore.getState().selectedProject;
    void reportOpenSession(sel ? { ServerId: sel.serverId, ProjectId: sel.projectId } : null);
  }, [open]);
}

/** Whether this window is in front: shown, and focused. On the phone, the app in the foreground. */
const inFront = (): boolean => document.visibilityState === 'visible' && document.hasFocus();

/** `inFront`, again whenever the window gains or loses focus, or is shown or hidden. */
function useInFront(): boolean {
  const [front, setFront] = useState(inFront);
  useEffect(() => {
    const update = () => setFront(inFront());
    window.addEventListener('focus', update);
    window.addEventListener('blur', update);
    document.addEventListener('visibilitychange', update);
    return () => {
      window.removeEventListener('focus', update);
      window.removeEventListener('blur', update);
      document.removeEventListener('visibilitychange', update);
    };
  }, []);
  return front;
}

/**
 * Opening a project settles its item (#592): while the window is in front and its main view shows a session with an
 * item, the server is told it was opened (MarkOpened), and sees what is on screen where the user lands, by kind. Once
 * for each item it shows, and again when the user comes back to it: one the server leaves (an error, a review, an
 * earlier escalation) is not asked about over and over. An item it has acknowledged has nothing more to settle.
 */
export function useMarkOpened(): void {
  const open = useAppStore(openSessionKey);
  const front = useInFront();
  const item = useAppStore(s => open === null ? undefined : s.attention.find(i => projectKey(i.serverId, i.ProjectId) === open));
  const what = front && item && !item.Acknowledged ? `${open}|${item.Kind}|${item.Since}` : null;
  const settled = useRef<string | null>(null);
  useEffect(() => {
    if (what === settled.current) return;
    settled.current = what;
    if (!what || !item) return;
    useAppStore.getState().markOpened(item.serverId, item.ProjectId)
      .catch(err => console.debug('[openSession] MarkOpened failed:', err));
  }, [what, item]);
}
