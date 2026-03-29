/**
 * Unified API client for the GodMode backend.
 *
 * React always talks to a server that provides REST endpoints and a SignalR hub.
 * Two hosting modes, identical from React's perspective:
 *
 *  - MAUI: base URL injected via window.__GODMODE_BASE_URL__ (local proxy that
 *    relays to remote servers). Hub connection goes through the relay.
 *  - Server-hosted: base URL is window.location.origin (GodMode.Server serves
 *    the React client AND is the project hub). Hub connects directly.
 *
 * The only behavioral difference is how the SignalR hub URL is constructed:
 *  - MAUI relay: {baseUrl}/?serverId={id} (skipNegotiation, WebSocket-only)
 *  - Direct:     {baseUrl}/hubs/projects  (standard negotiation)
 */
import type { ServerInfo } from '../signalr/types';

// ── Public types ───────────────────────────────────────────────

export interface AddServerRequest {
  DisplayName: string;
  Url: string;
  AccessToken?: string | null;
  Type?: string;
  Username?: string | null;
}

// ── Mode detection ─────────────────────────────────────────────

declare global {
  interface Window {
    __GODMODE_BASE_URL__?: string;
  }
}

/** HybridWebView serves from 0.0.0.1 — if we're on that host, we're in MAUI. */
export const isMaui = window.location.hostname === '0.0.0.1';

// ── Unified API ────────────────────────────────────────────────

function getBaseUrl(): string {
  if (isMaui) return window.__GODMODE_BASE_URL__ || '';
  return window.location.origin;
}

/** Wait for the base URL to be available (MAUI injects it async). */
export async function waitUntilReady(timeoutMs = 5000): Promise<void> {
  if (!isMaui) return;
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (window.__GODMODE_BASE_URL__) return;
    await new Promise(r => setTimeout(r, 50));
  }
  console.warn('[api] Timed out waiting for MAUI base URL injection');
}

export async function fetchServers(): Promise<ServerInfo[]> {
  const res = await fetch(`${getBaseUrl()}/servers`);
  if (!res.ok) throw new Error(`Failed to fetch servers: ${res.status}`);
  return res.json();
}

export async function addServer(req: AddServerRequest): Promise<void> {
  const res = await fetch(`${getBaseUrl()}/servers/registrations`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`Failed to add server: ${res.status}`);
}

export async function removeServer(serverId: string): Promise<void> {
  // MAUI LocalServer uses index-based deletion — resolve serverId to index
  const servers = await fetchServers();
  const index = servers.findIndex(s => s.Id === serverId);
  if (index < 0) throw new Error(`Server not found: ${serverId}`);
  const res = await fetch(`${getBaseUrl()}/servers/registrations/${index}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to remove server: ${res.status}`);
}

export async function startServer(serverId: string): Promise<void> {
  const res = await fetch(`${getBaseUrl()}/servers/${encodeURIComponent(serverId)}/start`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to start server: ${res.status}`);
}

export async function stopServer(serverId: string): Promise<void> {
  const res = await fetch(`${getBaseUrl()}/servers/${encodeURIComponent(serverId)}/stop`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to stop server: ${res.status}`);
}

export async function openDevTools(): Promise<void> {
  await fetch(`${getBaseUrl()}/devtools`, { method: 'POST' });
}

export function subscribeEvents(onEvent: (type: string, data: unknown) => void): () => void {
  const baseUrl = getBaseUrl();
  if (!baseUrl) return () => {};
  const source = new EventSource(`${baseUrl}/events`);
  source.addEventListener('serversChanged', () => onEvent('serversChanged', null));
  source.onerror = () => {};
  return () => source.close();
}

// ── Hub connection helpers ─────────────────────────────────────

export function getHubUrl(serverId: string): string {
  const baseUrl = getBaseUrl();
  if (isMaui) {
    // MAUI proxy relay: route by serverId
    return `${baseUrl}/?serverId=${encodeURIComponent(serverId)}`;
  }
  // Server-hosted: hub is on the same origin
  return `${baseUrl}/hubs/projects`;
}

export function getHubOptions(_serverId: string): import('@microsoft/signalr').IHttpConnectionOptions {
  if (isMaui) {
    return {
      skipNegotiation: true,
      transport: 1, // signalR.HttpTransportType.WebSockets
    };
  }
  return {};
}
