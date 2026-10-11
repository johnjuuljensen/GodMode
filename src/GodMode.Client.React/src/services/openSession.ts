/**
 * The app told which session this window's main view shows (#549), so it holds back that session's notification while
 * the app is in front (Android's AttentionNotifier; Windows' toasts, #491, can ask the same). Over the bridge as voice
 * is: session.open, whenever the open session changes.
 */
import { useEffect } from 'react';
import * as bridge from './hostBridge';
import { useAppStore, openSessionKey } from '../store';

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
