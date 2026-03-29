/**
 * Server registration stored in localStorage.
 * Replaces GodMode.ClientBase ServerRegistryService.
 */

export interface ServerRegistration {
  url: string;
  displayName: string;
  /** Optional GitHub PAT for codespace port-forwarded servers */
  accessToken?: string;
}

const STORAGE_KEY = 'godmode-servers';

export function loadServers(): ServerRegistration[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch {
    return [];
  }
}

export function saveServers(servers: ServerRegistration[]) {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(servers));
}

export function addServer(server: ServerRegistration): ServerRegistration[] {
  const servers = loadServers();
  servers.push(server);
  saveServers(servers);
  return servers;
}

export function updateServer(url: string, server: ServerRegistration): ServerRegistration[] {
  const servers = loadServers();
  const index = servers.findIndex(s => s.url === url);
  if (index >= 0) {
    servers[index] = server;
    saveServers(servers);
  }
  return servers;
}

export function removeServer(url: string): ServerRegistration[] {
  const servers = loadServers();
  const index = servers.findIndex(s => s.url === url);
  if (index >= 0) {
    servers.splice(index, 1);
    saveServers(servers);
  }
  return servers;
}
