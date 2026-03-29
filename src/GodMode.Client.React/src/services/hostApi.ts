/**
 * Abstraction over host-provided services.
 *
 * Two implementations:
 *  - MauiHostApi  : delegates to the local MAUI proxy server (REST + SSE)
 *  - StandaloneHostApi : localStorage-backed server registry, no MAUI features
 *
 * Mode detection: HybridWebView serves from https://0.0.0.1, so we detect
 * MAUI mode synchronously by checking the hostname. The MAUI proxy base URL
 * is injected asynchronously via window.__GODMODE_BASE_URL__ and polled in
 * waitUntilReady().
 */
import type { ServerInfo } from '../signalr/types';

// ── Public contract ────────────────────────────────────────────

export interface AddServerRequest {
  DisplayName: string;
  Url: string;
  AccessToken?: string | null;
  Type?: string;
  Username?: string | null;
}

export interface IHostApi {
  readonly isStandalone: boolean;

  /** Wait for the host environment to be ready (MAUI base-URL injection, etc.). */
  waitUntilReady(): Promise<void>;

  fetchServers(): Promise<ServerInfo[]>;
  addServer(req: AddServerRequest): Promise<void>;
  removeServer(serverId: string): Promise<void>;
  startServer(serverId: string): Promise<void>;
  stopServer(serverId: string): Promise<void>;
  openDevTools(): Promise<void>;

  /** Subscribe to host-level events (e.g. server list changed). Returns unsubscribe fn. */
  subscribeEvents(onEvent: (type: string, data: unknown) => void): () => void;

  /** Retrieve an access token stored for a server (standalone only; MAUI returns null). */
  getAccessToken(serverId: string): string | null;

  /**
   * Build the SignalR hub URL for a given server.
   * MAUI routes through its local proxy; standalone connects directly.
   */
  getHubUrl(serverId: string): string;

  /**
   * SignalR connection options per mode.
   * MAUI needs skipNegotiation + WebSockets-only; standalone uses defaults.
   */
  getHubOptions(serverId: string): import('@microsoft/signalr').IHttpConnectionOptions;
}

// ── Mode detection ─────────────────────────────────────────────

/** HybridWebView serves from 0.0.0.1 — if we're on that host, we're in MAUI. */
const isMauiHost = window.location.hostname === '0.0.0.1';

declare global {
  interface Window {
    __GODMODE_BASE_URL__?: string;
  }
}

// ── MAUI implementation ────────────────────────────────────────

class MauiHostApi implements IHostApi {
  readonly isStandalone = false;

  async waitUntilReady(timeoutMs = 5000): Promise<void> {
    const start = Date.now();
    while (Date.now() - start < timeoutMs) {
      if (window.__GODMODE_BASE_URL__) return;
      await new Promise(r => setTimeout(r, 50));
    }
    console.warn('[hostApi] Timed out waiting for MAUI base URL injection');
  }

  async fetchServers(): Promise<ServerInfo[]> {
    const res = await fetch(`${window.__GODMODE_BASE_URL__!}/servers`);
    if (!res.ok) throw new Error(`Failed to fetch servers: ${res.status}`);
    return res.json();
  }

  async addServer(req: AddServerRequest): Promise<void> {
    const res = await fetch(`${window.__GODMODE_BASE_URL__!}/servers/registrations`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    });
    if (!res.ok) throw new Error(`Failed to add server: ${res.status}`);
  }

  async removeServer(serverId: string): Promise<void> {
    // MAUI LocalServer uses index-based deletion — resolve serverId to index
    const servers = await this.fetchServers();
    const index = servers.findIndex(s => s.Id === serverId);
    if (index < 0) throw new Error(`Server not found: ${serverId}`);
    const res = await fetch(`${window.__GODMODE_BASE_URL__!}/servers/registrations/${index}`, { method: 'DELETE' });
    if (!res.ok) throw new Error(`Failed to remove server: ${res.status}`);
  }

  async startServer(serverId: string): Promise<void> {
    const res = await fetch(`${window.__GODMODE_BASE_URL__!}/servers/${encodeURIComponent(serverId)}/start`, { method: 'POST' });
    if (!res.ok) throw new Error(`Failed to start server: ${res.status}`);
  }

  async stopServer(serverId: string): Promise<void> {
    const res = await fetch(`${window.__GODMODE_BASE_URL__!}/servers/${encodeURIComponent(serverId)}/stop`, { method: 'POST' });
    if (!res.ok) throw new Error(`Failed to stop server: ${res.status}`);
  }

  async openDevTools(): Promise<void> {
    await fetch(`${window.__GODMODE_BASE_URL__!}/devtools`, { method: 'POST' });
  }

  subscribeEvents(onEvent: (type: string, data: unknown) => void): () => void {
    const source = new EventSource(`${window.__GODMODE_BASE_URL__!}/events`);
    source.addEventListener('serversChanged', () => onEvent('serversChanged', null));
    source.onerror = () => {};
    return () => source.close();
  }

  getAccessToken(_serverId: string): string | null {
    return null; // MAUI handles tokens internally
  }

  getHubUrl(serverId: string): string {
    return `${window.__GODMODE_BASE_URL__!}/?serverId=${encodeURIComponent(serverId)}`;
  }

  getHubOptions(_serverId: string): import('@microsoft/signalr').IHttpConnectionOptions {
    return {
      skipNegotiation: true,
      transport: 1, // WebSockets only (signalR.HttpTransportType.WebSockets)
    };
  }
}

// ── Standalone implementation ──────────────────────────────────

const SERVERS_KEY = 'godmode-servers';

interface StoredServer {
  id: string;
  displayName: string;
  url: string;
  accessToken?: string | null;
}

function loadStoredServers(): StoredServer[] {
  try { return JSON.parse(localStorage.getItem(SERVERS_KEY) || '[]'); } catch { return []; }
}

function saveStoredServers(servers: StoredServer[]) {
  localStorage.setItem(SERVERS_KEY, JSON.stringify(servers));
}

let nextId = Date.now();

class StandaloneHostApi implements IHostApi {
  readonly isStandalone = true;

  async waitUntilReady(): Promise<void> {
    // No waiting needed in standalone mode
  }

  async fetchServers(): Promise<ServerInfo[]> {
    return loadStoredServers().map(s => ({
      Id: s.id,
      Name: s.displayName || s.url,
      Type: 'local',
      State: 'Running' as const,
      Url: s.url,
      Description: null,
    }));
  }

  async addServer(req: AddServerRequest): Promise<void> {
    const servers = loadStoredServers();
    servers.push({
      id: `standalone-${++nextId}`,
      displayName: req.DisplayName,
      url: req.Url,
      accessToken: req.AccessToken,
    });
    saveStoredServers(servers);
  }

  async removeServer(serverId: string): Promise<void> {
    const servers = loadStoredServers().filter(s => s.id !== serverId);
    saveStoredServers(servers);
  }

  async startServer(_serverId: string): Promise<void> {
    // No-op: standalone can't start/stop servers
  }

  async stopServer(_serverId: string): Promise<void> {
    // No-op
  }

  async openDevTools(): Promise<void> {
    // No-op: only available in MAUI
  }

  subscribeEvents(_onEvent: (type: string, data: unknown) => void): () => void {
    return () => {}; // No SSE in standalone mode
  }

  getAccessToken(serverId: string): string | null {
    const servers = loadStoredServers();
    return servers.find(s => s.id === serverId)?.accessToken ?? null;
  }

  getHubUrl(serverId: string): string {
    const servers = loadStoredServers();
    const server = servers.find(s => s.id === serverId);
    if (!server) throw new Error(`Unknown server: ${serverId}`);
    return server.url.replace(/\/$/, '') + '/hubs/projects';
  }

  getHubOptions(serverId: string): import('@microsoft/signalr').IHttpConnectionOptions {
    const token = this.getAccessToken(serverId);
    return token ? { accessTokenFactory: () => token } : {};
  }
}

// ── Singleton ──────────────────────────────────────────────────

export const hostApi: IHostApi = isMauiHost
  ? new MauiHostApi()
  : new StandaloneHostApi();
