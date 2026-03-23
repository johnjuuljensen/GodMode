/**
 * Host bridge — bidirectional communication between the React app and a native host.
 *
 * When running in a MAUI HybridWebView, messages flow through the HybridWebView
 * raw message channel. When running standalone in a browser, the bridge is null
 * and all host features are unavailable — the app works fully on its own.
 *
 * Supports three patterns:
 * - Fire-and-forget: bridge.send(type, payload)
 * - Request/response: bridge.request<T>(type, payload) → Promise<T>
 * - Events: bridge.on(type, handler) → unsubscribe
 */
import type { BridgeMessage } from './types';

type MessageHandler = (payload: unknown) => void;

export class HostBridge {
  private handlers = new Map<string, Set<MessageHandler>>();
  private pendingRequests = new Map<string, {
    resolve: (value: unknown) => void;
    reject: (reason: unknown) => void;
  }>();

  private constructor() {
    // Listen for messages from the MAUI host
    window.addEventListener('HybridWebViewMessageReceived', ((e: CustomEvent) => {
      this.handleMessage(e.detail.message);
    }) as EventListener);
  }

  /** Send a fire-and-forget message to the host. */
  send(type: string, payload?: unknown): void {
    const msg: BridgeMessage = { Type: type, Payload: payload };
    window.HybridWebView!.SendRawMessage(JSON.stringify(msg));
  }

  /** Send a request to the host and await a response. */
  request<T = unknown>(type: string, payload?: unknown, timeoutMs = 30000): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      const id = crypto.randomUUID().replace(/-/g, '');
      const msg: BridgeMessage = { Type: type, Id: id, Payload: payload };

      const timer = setTimeout(() => {
        this.pendingRequests.delete(id);
        reject(new Error(`Bridge request '${type}' timed out after ${timeoutMs}ms`));
      }, timeoutMs);

      this.pendingRequests.set(id, {
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value as T);
        },
        reject: (reason) => {
          clearTimeout(timer);
          reject(reason);
        },
      });

      window.HybridWebView!.SendRawMessage(JSON.stringify(msg));
    });
  }

  /** Subscribe to messages of a given type from the host. Returns an unsubscribe function. */
  on(type: string, handler: MessageHandler): () => void {
    let set = this.handlers.get(type);
    if (!set) {
      set = new Set();
      this.handlers.set(type, set);
    }
    set.add(handler);
    return () => set!.delete(handler);
  }

  private handleMessage(raw: string): void {
    let msg: BridgeMessage;
    try {
      msg = JSON.parse(raw);
    } catch (e) {
      console.warn('[HostBridge] Failed to parse message:', e);
      return;
    }

    // Route responses to pending requests
    if (msg.Id && this.pendingRequests.has(msg.Id)) {
      const pending = this.pendingRequests.get(msg.Id)!;
      this.pendingRequests.delete(msg.Id);
      pending.resolve(msg.Payload);
      return;
    }

    // Dispatch to event handlers
    const handlers = this.handlers.get(msg.Type);
    if (handlers) {
      for (const handler of handlers) {
        handler(msg.Payload);
      }
    }
  }

  /**
   * Create a bridge if running inside a MAUI HybridWebView host.
   * Returns null when running standalone in a browser.
   */
  static detect(): HostBridge | null {
    if (window.HybridWebView) {
      return new HostBridge();
    }
    return null;
  }
}

/** Returns true if a native host is available. */
export function hasHost(): boolean {
  return window.HybridWebView !== undefined;
}

/** Singleton bridge instance — null when standalone. */
let _bridge: HostBridge | null | undefined;

/** Get the host bridge (lazy-initialized). Returns null when standalone. */
export function getHostBridge(): HostBridge | null {
  if (_bridge === undefined) {
    _bridge = HostBridge.detect();
  }
  return _bridge;
}
