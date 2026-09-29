// Deleting a session, from its row in the list or from its view (#325). DeleteProject as it is (the
// root's delete script first, force for a running claude), and then:
// - a session that shares its working folder loses only its state, into the folder's trash: it goes at
//   once, and "Deleted · Undo" brings it back (RestoreProject), with no dialog;
// - any other loses its working folder and every file in it, which nothing brings back: a dialog names
//   the session and what goes, and asks first.
import { confirmAction } from './confirmDialog';
import { showToast } from './toast';
import { useAppStore } from './store';
import type { ProjectSummary } from './signalr/types';

const messageOf = (err: unknown) => err instanceof Error ? err.message.replace(/^.*HubException: /, '') : String(err);

/** What a worktree delete's dialog says goes: the session, its folder and files, and the root's script before that. */
export function worktreeDeleteMessage(project: ProjectSummary): string {
  const root = project.RootName ? `the ${project.RootName} root's` : "its root's";
  const running = project.State === 'Running' ? 'Claude is stopped first. ' : '';
  return `${running}This runs ${root} delete script, then removes the session's working folder, every file in it and the session's history. This cannot be undone.`;
}

/**
 * Deletes the session: at once when it shares its folder (with Undo), after a confirming dialog when its
 * folder goes. Resolves with whether it was deleted. A delete that fails (its script refuses, say) says why
 * in a toast.
 */
export async function deleteSession(serverId: string, project: ProjectSummary): Promise<boolean> {
  const store = useAppStore.getState();
  const hub = store.serverConnections.find(c => c.serverInfo.Id === serverId)?.hub;
  if (!hub) return false;

  const kind = project.Kind ? `${project.Kind} session` : 'session';
  if (!project.SharedFolder
    && !await confirmAction(`Delete the ${kind} "${project.Name}"?`, 'Delete', { message: worktreeDeleteMessage(project), tone: 'danger' }))
    return false;

  let trashed: boolean;
  try {
    trashed = (await hub.deleteProject(project.Id, project.State === 'Running')).Trashed;
  } catch (err) {
    showToast({ text: `Could not delete "${project.Name}": ${messageOf(err)}`, tone: 'error' });
    return false;
  }

  // Who deleted it is done with it; a view it was deleted under says it is not found
  const selected = useAppStore.getState().selectedProject;
  if (selected?.serverId === serverId && selected.projectId === project.Id) useAppStore.getState().clearSelection();

  showToast(trashed
    ? { text: `Deleted "${project.Name}"`, action: { label: 'Undo', run: () => restoreSession(serverId, project) } }
    : { text: `Deleted "${project.Name}" and its folder` });
  return true;
}

/** Undo: the session is back under the same ID, which the server pushes as ProjectCreated. A restore that fails says why. */
export async function restoreSession(serverId: string, project: ProjectSummary): Promise<void> {
  const hub = useAppStore.getState().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub;
  try {
    if (!hub) throw new Error('its server is not connected');
    await hub.restoreProject(project.Id);
  } catch (err) {
    showToast({ text: `Could not restore "${project.Name}": ${messageOf(err)}`, tone: 'error' });
  }
}
