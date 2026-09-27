/**
 * The app around the page, for a test marked `// @vitest-environment jsdom`: the page is only ever the one the
 * GodMode app hosts, at the app's own address, with the app's shell on the other side of window.HybridWebView.
 * setup.ts installs it for every jsdom test, so the real hostApi and hostBridge run against it: a test answers
 * the requests it expects the page to make (`answer`), sends the shell's events (`emit`), and reads what the
 * page sent (`sent`). A request nobody answers fails as the shell fails an unknown type.
 */
import { act } from 'react';
import type { BridgeEvent, BridgeEvents, BridgeMessage, BridgeRequests } from '../services/hostBridge';

/** Where the app's WebView serves its page (HybridWebView, on Windows and Android). */
export const APP_PAGE_URL = 'https://0.0.0.1/';

type Answer<K extends keyof BridgeRequests> =
  BridgeRequests[K][1] | Error | ((payload: BridgeRequests[K][0]) => BridgeRequests[K][1] | Promise<BridgeRequests[K][1]>);

const answers = new Map<string, unknown>();
let unanswered = 0;

/** Every request the page sent the shell, oldest first. */
export const sent: { Type: string; Payload: unknown }[] = [];

/** The shell answers `type` with this value, or throws this Error's message back, or answers what the function returns. */
export function answer<K extends keyof BridgeRequests>(type: K, response: Answer<K>): void {
  answers.set(type, response);
}

/** The shell sends an event, as the app does (servers.changed, attention.open, voice.*). */
export const emit = <K extends BridgeEvent>(type: K, ...payload: BridgeEvents[K] extends void ? [] : [BridgeEvents[K]]) =>
  act(async () => deliver({ Type: type, Payload: payload[0] ?? null }));

function deliver(message: BridgeMessage): void {
  window.dispatchEvent(new CustomEvent('HybridWebViewMessageReceived', { detail: { message: JSON.stringify(message) } }));
}

async function respond({ Type, Id, Payload }: BridgeMessage): Promise<void> {
  if (!answers.has(Type)) return deliver({ Type, Id, Error: `Unknown message type: ${Type}` });
  const response = answers.get(Type);
  try {
    if (response instanceof Error) throw response;
    const value = typeof response === 'function' ? await (response as (p: unknown) => unknown)(Payload) : response;
    deliver({ Type, Id, Payload: value });
  } catch (err) {
    deliver({ Type, Id, Error: err instanceof Error ? err.message : String(err) });
  }
}

/** The page as the app hosts it: at the app's address, with the shell's HybridWebView. */
export function installAppShell(): void {
  if (window.location.href !== APP_PAGE_URL) throw new Error(`The page is at ${window.location.href}, not the app's ${APP_PAGE_URL}`);
  window.HybridWebView = {
    SendRawMessage(raw: string) {
      const message = JSON.parse(raw) as BridgeMessage;
      sent.push({ Type: message.Type, Payload: message.Payload ?? null });
      // The shell answers later, never within the call
      unanswered++;
      setTimeout(() => void respond(message).finally(() => unanswered--), 0);
    },
  };
}

/** Waits, within act(), until the shell has answered everything the page asked, and what those answers led the page to ask. */
export const settle = () => act(async () => {
  for (let ticks = 0; ticks < 3 || unanswered > 0; ticks++) await new Promise(resolve => setTimeout(resolve, 0));
});

/** Before each test: nothing answered yet, nothing sent. */
export function resetAppShell(): void {
  answers.clear();
  sent.length = 0;
}
