/**
 * The page's host: the GodMode app, the only one there is (the server serves no page).
 *
 * The app's local relay forwards hub connections to the registered servers. React learns the relay's
 * URL and per-launch secret from the host bridge (hostBridge.ts, `relay.info`), and manages servers
 * over the bridge too. A hub URL is {relayUrl}/?serverId={id}, WebSocket-only with no negotiation,
 * and the relay's secret goes on it as access_token.
 *
 * Authentication: the app keeps each server's key in secure storage and the relay adds it when
 * relaying. React hands a key over once, when adding a server, and never gets it back. It sends the
 * relay only the relay's own secret.
 */
import type { AddServerRequest, ServerInfo } from '../signalr/types';
import * as bridge from './hostBridge';
import type { RelayInfo, WindowInfo } from './hostBridge';

/** The relay's URL and secret, once the bridge has answered relay.info. */
let relayInfo: RelayInfo | null = null;
let relayInfoRequest: Promise<void> | null = null;

/** Asks the app for the relay's URL and secret (once). */
export async function waitUntilReady(timeoutMs = 5000): Promise<void> {
  if (relayInfo) return;
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
    console.warn('[api] Could not get the relay info from the app:', err);
  } finally {
    clearTimeout(timer);
  }
}

/** Splits "url1, url2" into its URLs. */
function splitUrls(urls: string): string[] {
  return urls.split(/[\s,]+/).filter(u => u.length > 0);
}

export const fetchServers = (): Promise<ServerInfo[]> => bridge.request('servers.list');

/** `req.Url` may hold several URLs separated by commas or spaces, in order of preference. */
export async function addServer(req: AddServerRequest): Promise<void> {
  await bridge.request('servers.add', {
    Type: req.Type,
    DisplayName: req.DisplayName,
    Urls: splitUrls(req.Url),
    Username: req.Username ?? null,
    AccessToken: req.AccessToken ?? null,
  });
}

export async function removeServer(serverId: string): Promise<void> {
  await bridge.request('servers.remove', { ServerId: serverId });
}

export async function startServer(serverId: string): Promise<void> {
  await bridge.request('servers.start', { ServerId: serverId });
}

export async function stopServer(serverId: string): Promise<void> {
  await bridge.request('servers.stop', { ServerId: serverId });
}

export async function openDevTools(): Promise<void> {
  await bridge.request('host.openDevTools');
}

/** The main window's, where the app does not answer: unlocked, and no windows to open. */
const MAIN_WINDOW: WindowInfo = { Profile: null, CanOpenWindows: false };

/** Which window this page is in (#340), asked of the app. */
export const windowInfo = (): Promise<WindowInfo> => bridge.request('window.info').catch(err => {
  console.warn('[api] Could not get the window info from the app, so the page is unlocked:', err);
  return MAIN_WINDOW;
});

export async function openProfileWindow(profile: string): Promise<void> {
  await bridge.request('window.openProfile', { Profile: profile });
}

export function subscribeEvents(onEvent: (type: string, data: unknown) => void): () => void {
  return bridge.on('servers.changed', () => onEvent('serversChanged', null));
}

/**
 * Calls `open` with the item each tapped notification names (the Android app's; see AttentionNotifier),
 * including the tap that launched the app, which came before this page loaded.
 */
export function subscribeAttentionLinks(open: (serverId: string, projectId: string) => void): () => void {
  const take = () => bridge.request('attention.take')
    .then(link => { if (link) open(link.ServerId, link.ProjectId); })
    .catch(err => console.error('[hostApi] attention.take failed:', err));
  const unsubscribe = bridge.on('attention.open', take);
  take();
  return unsubscribe;
}

// ── Hub connection helpers ─────────────────────────────────────

/** The relay's address for a server's hub: it routes by serverId. */
export function getHubUrl(serverId: string): string {
  return `${relayInfo?.BaseUrl ?? ''}/?serverId=${encodeURIComponent(serverId)}`;
}

export function getHubOptions(_serverId: string): import('@microsoft/signalr').IHttpConnectionOptions {
  return {
    skipNegotiation: true,
    transport: 1, // signalR.HttpTransportType.WebSockets
    // The relay's per-launch secret; SignalR puts it on the WebSocket URL as access_token.
    accessTokenFactory: () => relayInfo?.Secret ?? '',
  };
}
