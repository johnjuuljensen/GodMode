/**
 * A project's identity across servers. Project IDs are unique only within one server, so every
 * per-project map in the store is keyed by this, never by the project ID alone.
 */
export type ProjectKey = string & { readonly __projectKey: unique symbol };

/** The key of a project on a server. Project IDs contain '/', so a key is only ever built, never split. */
export const projectKey = (serverId: string, projectId: string) => `${serverId}:${projectId}` as ProjectKey;
