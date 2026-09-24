/**
 * Unified API client for the GodMode backend.
 *
 * React always talks to a server that provides REST endpoints and a SignalR hub.
 * Two hosting modes, identical from React's perspective:
 *
 *  - MAUI: the shell's local relay forwards hub connections to the registered servers.
 *    React learns the relay's URL and per-launch secret from the host bridge
 *    (hostBridge.ts, `relay.info`), and manages servers over the bridge too.
 *  - Server-hosted: base URL is window.location.origin (GodMode.Server serves
 *    the React client AND is the project hub). Hub connects directly.
 *
 * The only behavioral difference is how the SignalR hub URL is constructed:
 *  - MAUI relay: {relayUrl}/?serverId={id}&access_token={secret} (skipNegotiation, WebSocket-only)
 *  - Direct:     {baseUrl}/hubs/projects  (standard negotiation)
 *
 * Authentication: the server requires its API key on every request except /health
 * and the client's static files. In MAUI the shell keeps each server's key in secure
 * storage and the relay adds it when relaying; React hands a key over once, when adding
 * a server, and never gets it back. It sends the relay only the relay's own secret.
 * Server-hosted, the user enters the key once; it is kept in this browser's
 * localStorage and sent as a bearer token (HTTP header, hub accessTokenFactory).
 */
import type { ServerInfo } from '../signalr/types';
import * as bridge from './hostBridge';
import type { RelayInfo } from './hostBridge';

// ── Public types ───────────────────────────────────────────────

export interface AddServerRequest {
  DisplayName: string;
  /** In MAUI, several URLs may be given separated by commas or spaces, in order of preference. */
  Url: string;
  /** Local server URLs in order of preference (MAUI); overrides Url. */
  Urls?: string[] | null;
  AccessToken?: string | null;
  Type?: string;
  Username?: string | null;
}

// ── Mode detection ─────────────────────────────────────────────

/** HybridWebView serves from 0.0.0.1 — if we're on that host, we're in MAUI. */
export const isMaui = window.location.hostname === '0.0.0.1';

// ── API key (server-hosted mode only) ──────────────────────────

const API_KEY_STORAGE = 'godmode-api-key';

/** Holds the key when localStorage is unavailable (private window, blocked site data); lost on reload. */
let memoryKey: string | null = null;

/** The entered API key, or null in MAUI mode or when none has been entered. */
export function getApiKey(): string | null {
  if (isMaui) return null;
  try {
    return localStorage.getItem(API_KEY_STORAGE) ?? memoryKey;
  } catch {
    return memoryKey;
  }
}

export function setApiKey(key: string): void {
  memoryKey = key;
  try {
    localStorage.setItem(API_KEY_STORAGE, key);
  } catch { /* kept in memory only */ }
}

export function clearApiKey(): void {
  memoryKey = null;
  try {
    localStorage.removeItem(API_KEY_STORAGE);
  } catch { /* ignore */ }
}

function authHeaders(): Record<string, string> {
  const key = getApiKey();
  return key ? { Authorization: `Bearer ${key}` } : {};
}

// ── Unified API ────────────────────────────────────────────────

/** The MAUI relay's URL and secret, once the bridge has answered relay.info. */
let relayInfo: RelayInfo | null = null;
let relayInfoRequest: Promise<void> | null = null;

function getBaseUrl(): string {
  if (isMaui) return relayInfo?.BaseUrl ?? '';
  return window.location.origin;
}

/** fetch against the backend, with the API key attached when one is held. */
function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  return fetch(`${getBaseUrl()}${path}`, {
    ...init,
    headers: { ...authHeaders(), ...init.headers },
  });
}

/**
 * Whether the backend accepts this client. False means the server wants an API key
 * (none entered yet, or the stored one was rejected). MAUI's local proxy never asks.
 */
export async function isAuthorized(): Promise<boolean> {
  if (isMaui) return true;
  const res = await apiFetch('/servers');
  return res.status !== 401;
}

/** In MAUI, ask the shell for the relay's URL and secret (once). */
export async function waitUntilReady(timeoutMs = 5000): Promise<void> {
  if (!isMaui || relayInfo) return;
  relayInfoRequest ??= bridge.request('relay.info')
    .then(info => { relayInfo = info; })
    .finally(() => { relayInfoRequest = null; });
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<void>((_, reject) => {
    timer = setTimeout(() => reject(new Error('timed out')), timeoutMs);
  });
  try {
    await Promise.race([relayInfoRequest, timeout]);
  } catch (err) {
    console.warn('[api] Could not get the relay info from the MAUI shell:', err);
  } finally {
    clearTimeout(timer);
  }
}

/** Splits "url1, url2" into its URLs. */
function splitUrls(urls: string): string[] {
  return urls.split(/[\s,]+/).filter(u => u.length > 0);
}

export async function fetchServers(): Promise<ServerInfo[]> {
  if (isMaui) return bridge.request('servers.list');
  const res = await apiFetch('/servers');
  if (!res.ok) throw new Error(`Failed to fetch servers: ${res.status}`);
  return res.json();
}

export async function addServer(req: AddServerRequest): Promise<void> {
  if (isMaui) {
    await bridge.request('servers.add', {
      Type: req.Type ?? 'local',
      DisplayName: req.DisplayName,
      Urls: req.Urls ?? splitUrls(req.Url),
      Username: req.Username ?? null,
      AccessToken: req.AccessToken ?? null,
    });
    return;
  }
  const res = await apiFetch('/servers/registrations', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`Failed to add server: ${res.status}`);
}

export async function removeServer(serverId: string): Promise<void> {
  if (isMaui) {
    await bridge.request('servers.remove', { ServerId: serverId });
    return;
  }
  const servers = await fetchServers();
  const index = servers.findIndex(s => s.Id === serverId);
  if (index < 0) throw new Error(`Server not found: ${serverId}`);
  const res = await apiFetch(`/servers/registrations/${index}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to remove server: ${res.status}`);
}

export async function startServer(serverId: string): Promise<void> {
  if (isMaui) {
    await bridge.request('servers.start', { ServerId: serverId });
    return;
  }
  const res = await apiFetch(`/servers/${encodeURIComponent(serverId)}/start`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to start server: ${res.status}`);
}

export async function stopServer(serverId: string): Promise<void> {
  if (isMaui) {
    await bridge.request('servers.stop', { ServerId: serverId });
    return;
  }
  const res = await apiFetch(`/servers/${encodeURIComponent(serverId)}/stop`, { method: 'POST' });
  if (!res.ok) throw new Error(`Failed to stop server: ${res.status}`);
}

export async function openDevTools(): Promise<void> {
  if (isMaui) {
    await bridge.request('host.openDevTools');
    return;
  }
  await apiFetch('/devtools', { method: 'POST' });
}

export function subscribeEvents(onEvent: (type: string, data: unknown) => void): () => void {
  // Only MAUI's local proxy emits server-list changes. GodMode.Server's /events is a
  // placeholder that never does, and EventSource cannot send the API key header.
  if (!isMaui) return () => {};
  return bridge.on('servers.changed', () => onEvent('serversChanged', null));
}

/**
 * Calls `open` with the item each tapped notification names (the Android shell's; see AttentionNotifier),
 * including the tap that launched the app, which came before this page loaded. Elsewhere, never.
 */
export function subscribeAttentionLinks(open: (serverId: string, projectId: string) => void): () => void {
  if (!isMaui) return () => {};
  const take = () => bridge.request('attention.take')
    .then(link => { if (link) open(link.ServerId, link.ProjectId); })
    .catch(err => console.error('[hostApi] attention.take failed:', err));
  const unsubscribe = bridge.on('attention.open', take);
  take();
  return unsubscribe;
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
      // The relay's per-launch secret; SignalR puts it on the WebSocket URL as access_token.
      accessTokenFactory: () => relayInfo?.Secret ?? '',
    };
  }
  // No key (loopback server) → empty token, which SignalR does not send.
  return { accessTokenFactory: () => getApiKey() ?? '' };
}
