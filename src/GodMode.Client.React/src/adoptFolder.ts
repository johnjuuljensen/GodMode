// Adopting a folder that exists in a root (#370): the root's "Not in GodMode (N)" group lists what the
// server's ListUnmanaged gives, each with Adopt, which makes a session of it (AdoptFolder) and opens it.
// The server pushes it as ProjectCreated, so it is listed as any other session.
import { showToast } from './toast';
import { useAppStore } from './store';
import type { UnmanagedFolder } from './signalr/types';

const messageOf = (err: unknown) => err instanceof Error ? err.message.replace(/^.*HubException: /, '') : String(err);

const hubOf = (serverId: string) => useAppStore.getState().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub;

/** The root's folders no session works in, as the server reads them now. Rejects, saying why, when its list script fails. */
export async function listUnmanaged(serverId: string, profileName: string, rootName: string): Promise<UnmanagedFolder[]> {
  const hub = hubOf(serverId);
  if (!hub) throw new Error('its server is not connected');
  try {
    return await hub.listUnmanaged(profileName, rootName);
  } catch (err) {
    throw new Error(messageOf(err));
  }
}

/**
 * Adopts the folder as a session of the root, with the action and inputs its listing gave, and its name and
 * kind for a session no script names; then opens it. Resolves with whether it was adopted; a refusal says why in a toast.
 */
export async function adoptFolder(serverId: string, profileName: string, rootName: string, folder: UnmanagedFolder): Promise<boolean> {
  const hub = hubOf(serverId);
  const inputs: Record<string, unknown> = { name: folder.Name, ...(folder.Kind ? { kind: folder.Kind } : {}), ...(folder.Inputs ?? {}) };
  try {
    if (!hub) throw new Error('its server is not connected');
    const status = await hub.adoptFolder(profileName, rootName, folder.Path, folder.ActionName ?? null, inputs);
    useAppStore.getState().openCreatedProject(serverId, status);
    return true;
  } catch (err) {
    showToast({ text: `Could not adopt "${folder.Name}": ${messageOf(err)}`, tone: 'error' });
    return false;
  }
}
