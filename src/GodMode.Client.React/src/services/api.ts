/**
 * REST API client.
 * When running inside MAUI, calls the local proxy server.
 * When running standalone in a browser, uses localStorage for server management.
 */
import type { HostInfo } from '../signalr/types';

declare global {
  interface Window {
    __GODMODE_BASE_URL__?: string;
  }
}

export function getBaseUrl(): string {
  return window.__GODMODE_BASE_URL__ || '';
}

/** True when running standalone (not inside MAUI). */
export function isStandalone(): boolean {
  return !window.__GODMODE_BASE_URL__;
}

export async function waitForBaseUrl(timeoutMs = 5000): Promise<string> {
  if (isStandalone()) return ''; // standalone mode doesn't need a base URL
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const url = getBaseUrl();
    if (url) return url;
    await new Promise(r => setTimeout(r, 50));
  }
  return '';
}

export interface AddServerRequest {
  DisplayName: string;
  Url: string;
  AccessToken?: string | null;
  Type?: string;
  Username?: string | null;
}

// ── localStorage-backed server registry for standalone mode ────

const SERVERS_KEY = 'godmode-servers';

interface StoredServer {
  DisplayName: string;
  Url: string;
  AccessToken?: string | null;
  Type?: string;
}

function loadStoredServers(): StoredServer[] {
  try { return JSON.parse(localStorage.getItem(SERVERS_KEY) || '[]'); } catch { return []; }
}

function saveStoredServers(servers: StoredServer[]) {
  localStorage.setItem(SERVERS_KEY, JSON.stringify(servers));
}

function storedToHostInfo(s: StoredServer, _index: number): HostInfo {
  return {
    Id: s.Url, // use URL as stable ID
    Name: s.DisplayName || s.Url,
    Type: s.Type || 'local',
    State: 'Running', // assume reachable until proven otherwise
    Url: s.Url,
    Description: null,
  };
}

// ── Unified API (delegates to MAUI proxy or localStorage) ──────

export async function fetchServers(): Promise<HostInfo[]> {
  if (isStandalone()) {
    return loadStoredServers().map(storedToHostInfo);
  }
  const res = await fetch(`${getBaseUrl()}/servers`);
  if (!res.ok) throw new Error(`Failed to fetch servers: ${res.status}`);
  return res.json();
}

export async function addServer(req: AddServerRequest): Promise<void> {
  if (isStandalone()) {
    const servers = loadStoredServers();
    servers.push({ DisplayName: req.DisplayName, Url: req.Url, AccessToken: req.AccessToken, Type: req.Type });
    saveStoredServers(servers);
    return;
  }
  const res = await fetch(`${getBaseUrl()}/servers/registrations`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`Failed to add server: ${res.status}`);
}

export async function removeServer(indexOrId: number | string): Promise<void> {
  if (isStandalone()) {
    const servers = loadStoredServers();
    const idx = typeof indexOrId === 'string'
      ? servers.findIndex(s => s.Url === indexOrId)
      : indexOrId;
    if (idx >= 0) {
      servers.splice(idx, 1);
      saveStoredServers(servers);
    }
    return;
  }
  const res = await fetch(`${getBaseUrl()}/servers/registrations/${indexOrId}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to remove server: ${res.status}`);
}

export async function startServer(serverId: string): Promise<void> {
  if (isStandalone()) return; // no-op in standalone
  const res = await fetch(`${getBaseUrl()}/servers/${encodeURIComponent(serverId)}/start`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to start server: ${res.status}`);
}

export async function stopServer(serverId: string): Promise<void> {
  if (isStandalone()) return;
  const res = await fetch(`${getBaseUrl()}/servers/${encodeURIComponent(serverId)}/stop`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to stop server: ${res.status}`);
}

export async function openDevTools(): Promise<void> {
  if (isStandalone()) return;
  await fetch(`${getBaseUrl()}/devtools`, { method: 'POST' });
}

export function subscribeEvents(onEvent: (type: string, data: unknown) => void): () => void {
  if (isStandalone()) return () => {}; // no SSE in standalone mode
  const baseUrl = getBaseUrl();
  if (!baseUrl) return () => {};

  const source = new EventSource(`${baseUrl}/events`);
  source.addEventListener('serversChanged', () => onEvent('serversChanged', null));
  source.onerror = () => {
    // Auto-reconnects by default; suppress console noise
  };
  return () => source.close();
}

/** Get the access token for a server (standalone mode only). */
export function getServerAccessToken(serverId: string): string | null {
  if (!isStandalone()) return null;
  const servers = loadStoredServers();
  const server = servers.find(s => s.Url === serverId);
  return server?.AccessToken ?? null;
}
