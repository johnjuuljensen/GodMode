// Deleting a session, from its row in the list or from its view (#325). DeleteProject as it is (the
// root's delete script first, never forced unless the user chooses it after a refusal), and then:
// - a session that shares its working folder loses only its state, into the folder's trash: it goes at
//   once, and "Deleted · Undo" brings it back (RestoreProject), with no dialog;
// - any other loses its working folder and every file in it, which nothing brings back: a dialog names
//   the session and what goes, and asks first.
// An adopted session (#370) was a folder before GodMode had it: its delete always asks, and offers
// "Forget (keep the folder)" beside Delete, which runs no delete script and moves only its state to the
// trash, with "Forgot · Undo".
import { askConfirm, confirmAction } from './confirmDialog';
import { showToast } from './toast';
import { useAppStore } from './store';
import type { ProjectSummary } from './signalr/types';
import { hubErrorMessage } from './signalr/hubError';


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
  if (project.Adopted) {
    const choice = await askConfirm({
      title: `Delete the ${kind} "${project.Name}"?`,
      message: adoptedDeleteMessage(project),
      choices: [
        { label: 'Forget (keep the folder)', value: 'forget', tone: 'secondary' },
        { label: 'Delete', value: 'delete', tone: 'danger' },
      ],
    });
    if (choice === null) return false;
    if (choice === 'forget') return forgetSession(serverId, project);
  } else if (!project.SharedFolder
    && !await confirmAction(`Delete the ${kind} "${project.Name}"?`, 'Delete', { message: worktreeDeleteMessage(project), tone: 'danger' }))
    return false;

  return runDelete(serverId, project, false);
}

/**
 * The delete itself. Never forced on its own: the server stops a running claude first, and the delete script
 * checks what it would lose (work not committed). A refusal says why, with "Force delete…", which asks
 * before it tells the script to go ahead anyway (GODMODE_FORCE).
 */
async function runDelete(serverId: string, project: ProjectSummary, force: boolean): Promise<boolean> {
  const hub = useAppStore.getState().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub;
  let trashed: boolean;
  try {
    if (!hub) throw new Error('its server is not connected');
    trashed = (await hub.deleteProject(project.Id, force)).Trashed;
  } catch (err) {
    const reason = hubErrorMessage(err);
    showToast(force
      ? { text: `Could not delete "${project.Name}": ${reason}`, tone: 'error' }
      : { text: `Could not delete "${project.Name}": ${reason}`, tone: 'error', action: { label: 'Force delete…', run: () => void forceDelete(serverId, project, reason) } });
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

/** A force delete, only ever chosen: after a refusal, and confirmed with what it may lose. */
async function forceDelete(serverId: string, project: ProjectSummary, reason: string): Promise<boolean> {
  if (!await confirmAction(`Force the delete of "${project.Name}"?`, 'Force delete', {
    message: `The delete was refused: ${reason} A force delete tells the root's delete script to go ahead anyway, and may lose work that is not committed.`,
    tone: 'danger',
  })) return false;
  return runDelete(serverId, project, true);
}

/** What an adopted session's delete dialog says: what Delete does, as for any session of its root, and that Forget keeps the folder. */
export function adoptedDeleteMessage(project: ProjectSummary): string {
  const deletes = project.SharedFolder
    ? "Delete runs its root's delete script, and moves the session's history to the trash."
    : worktreeDeleteMessage(project);
  return `${deletes} Forget runs no script and keeps the folder as it is: only the session's history goes, and Undo brings it back.`;
}

/** Forget: the session leaves GodMode, its folder stays, and "Forgot · Undo" brings it back. Resolves with whether it went. */
export async function forgetSession(serverId: string, project: ProjectSummary): Promise<boolean> {
  const hub = useAppStore.getState().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub;
  try {
    if (!hub) throw new Error('its server is not connected');
    await hub.forgetProject(project.Id);
  } catch (err) {
    showToast({ text: `Could not forget "${project.Name}": ${hubErrorMessage(err)}`, tone: 'error' });
    return false;
  }

  const selected = useAppStore.getState().selectedProject;
  if (selected?.serverId === serverId && selected.projectId === project.Id) useAppStore.getState().clearSelection();
  showToast({ text: `Forgot "${project.Name}"; its folder stays`, action: { label: 'Undo', run: () => restoreSession(serverId, project) } });
  return true;
}

/** Undo: the session is back under the same ID, which the server pushes as ProjectCreated. A restore that fails says why. */
export async function restoreSession(serverId: string, project: ProjectSummary): Promise<void> {
  const hub = useAppStore.getState().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub;
  try {
    if (!hub) throw new Error('its server is not connected');
    await hub.restoreProject(project.Id);
  } catch (err) {
    showToast({ text: `Could not restore "${project.Name}": ${hubErrorMessage(err)}`, tone: 'error' });
  }
}
