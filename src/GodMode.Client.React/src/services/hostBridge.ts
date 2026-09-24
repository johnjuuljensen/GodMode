/**
 * Typed request/response channel to the MAUI shell, over HybridWebView's raw messages.
 * Used only in MAUI mode (see hostApi.ts). The C# side is GodMode.Maui/Bridge
 * (HostBridge, ShellBridge, ShellMessages.cs); keep the message types in sync.
 *
 * Envelope (JSON, PascalCase like GodMode.Shared): { Type, Id?, Payload?, Error? }.
 * A request carries an Id; the shell answers with the same Type and Id, and either a
 * Payload or an Error. A message without an Id is an event (e.g. servers.changed).
 *
 * No message from the shell carries a server's access token.
 */
import type { ServerInfo } from '../signalr/types';

/** The relay's base URL and the per-launch secret it requires (as the access_token query parameter). */
export interface RelayInfo {
  BaseUrl: string;
  Secret: string;
}

export interface AddServerPayload {
  Type: string;
  DisplayName?: string | null;
  /** Local server URLs, in order of preference. */
  Urls?: string[] | null;
  Username?: string | null;
  AccessToken?: string | null;
}

interface ServerIdPayload {
  ServerId: string;
}

/** Request types → [payload, response]. */
interface BridgeRequests {
  'relay.info': [void, RelayInfo];
  'servers.list': [void, ServerInfo[]];
  'servers.add': [AddServerPayload, { Id: string }];
  'servers.remove': [ServerIdPayload, boolean];
  'servers.start': [ServerIdPayload, boolean];
  'servers.stop': [ServerIdPayload, boolean];
  'host.openDevTools': [void, boolean];
}

/** Event types the shell sends. */
export type BridgeEvent = 'servers.changed';

interface BridgeMessage {
  Type: string;
  Id?: string | null;
  Payload?: unknown;
  Error?: string | null;
}

declare global {
  interface Window {
    HybridWebView?: { SendRawMessage(message: string): void };
  }
}

const REQUEST_TIMEOUT_MS = 30_000;

let nextId = 0;
const pending = new Map<string, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
const listeners = new Map<string, Set<() => void>>();
let listening = false;

function ensureListening(): void {
  if (listening) return;
  listening = true;
  window.addEventListener('HybridWebViewMessageReceived', (e: Event) => {
    const raw = (e as CustomEvent<{ message?: unknown }>).detail?.message;
    if (typeof raw !== 'string') return;
    let msg: BridgeMessage;
    try {
      msg = JSON.parse(raw) as BridgeMessage;
    } catch {
      return; // not a bridge message
    }
    if (msg.Id) {
      const waiter = pending.get(msg.Id);
      if (!waiter) return;
      pending.delete(msg.Id);
      if (msg.Error) waiter.reject(new Error(msg.Error));
      else waiter.resolve(msg.Payload);
      return;
    }
    listeners.get(msg.Type)?.forEach(fn => fn());
  });
}

/** Sends a request to the shell and resolves with its response payload. */
export function request<K extends keyof BridgeRequests>(
  type: K,
  ...payload: BridgeRequests[K][0] extends void ? [] : [BridgeRequests[K][0]]
): Promise<BridgeRequests[K][1]> {
  ensureListening();
  const host = window.HybridWebView;
  if (!host) return Promise.reject(new Error('HybridWebView bridge is not available'));

  const id = `js-${++nextId}`;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Bridge request ${type} timed out`));
    }, REQUEST_TIMEOUT_MS);
    pending.set(id, {
      resolve: v => { clearTimeout(timer); resolve(v as BridgeRequests[K][1]); },
      reject: e => { clearTimeout(timer); reject(e); },
    });
    const message: BridgeMessage = { Type: type, Id: id, Payload: payload[0] ?? null };
    host.SendRawMessage(JSON.stringify(message));
  });
}

/** Subscribes to a shell event. Returns the unsubscribe function. */
export function on(type: BridgeEvent, handler: () => void): () => void {
  ensureListening();
  let set = listeners.get(type);
  if (!set) listeners.set(type, set = new Set());
  set.add(handler);
  return () => { set.delete(handler); };
}
