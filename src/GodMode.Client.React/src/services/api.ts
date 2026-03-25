/**
 * REST API client for the local MAUI proxy server.
 * Base URL is injected by MAUI via window.__GODMODE_BASE_URL__.
 */
import type { ServerInfo } from '../signalr/types';

declare global {
  interface Window {
    __GODMODE_BASE_URL__?: string;
  }
}

export function getBaseUrl(): string {
  return window.__GODMODE_BASE_URL__ || '';
}

export async function waitForBaseUrl(timeoutMs = 5000): Promise<string> {
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

export async function removeServer(index: number): Promise<void> {
  const res = await fetch(`${getBaseUrl()}/servers/registrations/${index}`, { method: 'DELETE' });
  if (!res.ok) throw new Error(`Failed to remove server: ${res.status}`);
}
