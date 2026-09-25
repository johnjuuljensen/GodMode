// @vitest-environment jsdom
/**
 * A server that is not connected (#221): its inbox items show offline while it reconnects, and a reply
 * to one says the server is offline, keeping the draft. A server that leaves the list, here or in
 * another client, is disconnected, so its hub stops retrying. One down when the page loads shows
 * reconnecting while its hub retries it. Drives the real store and Inbox through fake hubs.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, ServerInfo } from '../signalr/types';
import { FakeHub, connectServers, flush } from '../test/fakeHub';
import { render, typeInto, click, type Rendered } from '../test/render';
import { Inbox } from '../components/Inbox/Inbox';
import { useAppStore, projectKey } from './index';

const host = vi.hoisted(() => ({ servers: [] as ServerInfo[] }));

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => host.servers,
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
  isMaui: false,
  clearApiKey: () => {},
}));

const item = (projectId: string, kind: AttentionItem['Kind']): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: '2026-09-24T10:00:00Z', Text: `${projectId} ${kind}`,
});
const info = (id: string) => ({ Id: id, Name: `Server ${id}`, Type: 'local', State: 'Running' }) as ServerInfo;

const initialState = useAppStore.getState();
let hubA: FakeHub;
let hubB: FakeHub;
let view: Rendered | undefined;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  host.servers = [info('A'), info('B')];
  hubA = new FakeHub([], []);
  hubB = new FakeHub([], []);
  await connectServers({ A: hubA, B: hubB });
  await act(async () => {
    hubA.callbacks.onAttentionChanged?.([item('p1', 'Question')]);
    hubB.callbacks.onAttentionChanged?.([item('p2', 'Error')]);
  });
});

afterEach(() => { view?.unmount(); view = undefined; });

const itemEl = (name: string) => [...view!.container.querySelectorAll<HTMLElement>('.inbox-item')]
  .find(el => el.querySelector('.inbox-item-name')?.textContent === name)!;
const button = (el: HTMLElement, label: string) => [...el.querySelectorAll('button')].find(b => b.textContent === label)!;

describe('an inbox item whose server is reconnecting', () => {
  beforeEach(async () => {
    view = await render(<Inbox variant="screen" />);
    await act(() => hubA.drop());
  });

  it('is kept, and shows offline; the other server\'s is not', () => {
    expect(itemEl('p1').classList).toContain('inbox-item-offline');
    expect(itemEl('p1').querySelector('.inbox-item-meta')?.textContent).toContain('offline');
    expect(itemEl('p2').classList).not.toContain('inbox-item-offline');
  });

  it('says the server is offline on a reply, not SignalR\'s message, and keeps the draft', async () => {
    const el = itemEl('p1');
    await typeInto(el.querySelector('textarea')!, 'Use SQLite');
    await click(button(el, 'Send'));
    expect(el.querySelector('.inbox-item-error')?.textContent).toBe('Server offline: Server A is reconnecting. Try again once it is back');
    expect(useAppStore.getState().inboxDrafts[projectKey('A', 'p1')]?.reply).toBe('Use SQLite');
    expect(hubA.replies).toEqual([]);
  });

  it('is answerable again once its server is back', async () => {
    await act(() => hubA.reconnect());
    // The server's list, taken again on the reconnect, still has it
    await act(async () => hubA.callbacks.onAttentionChanged?.([item('p1', 'Question')]));
    const el = itemEl('p1');
    expect(el.classList).not.toContain('inbox-item-offline');
    await typeInto(el.querySelector('textarea')!, 'Use SQLite');
    await click(button(el, 'Send'));
    expect(hubA.replies).toEqual([{ projectId: 'p1', text: 'Use SQLite' }]);
  });
});

/** What serversChanged, sent when a server is added or removed in any client, does: the list is taken again. */
const serversChanged = () => act(async () => { await useAppStore.getState().loadServers(); await flush(); });

describe('a server that leaves the list', () => {
  it('removed in another client (serversChanged), is disconnected, so it no longer retries', async () => {
    await act(() => hubB.drop());
    host.servers = [info('A')];
    await serversChanged();
    expect(hubB.state).toBe('disconnected');
    expect(hubA.state).toBe('connected');
    expect(useAppStore.getState().serverConnections.map(c => c.serverInfo.Id)).toEqual(['A']);
  });

  it('a server still listed keeps its hub, connected', async () => {
    await serversChanged();
    expect(hubA.state).toBe('connected');
    expect(hubB.state).toBe('connected');
  });
});

/** A hub for a server down when the page loads: the first connect fails, and the hub goes on retrying it. */
class DownHub extends FakeHub {
  async connect() {
    this.calls.connect++;
    this.callbacks.onStateChanged?.('connecting');
    this.state = 'reconnecting';
    this.callbacks.onStateChanged?.('reconnecting');
    throw new Error('unreachable');
  }
}

describe('a server down when the page loads', () => {
  it('shows reconnecting while its hub retries, not disconnected, and connects when its retry does', async () => {
    const down = new DownHub([], []);
    await connectServers({ C: down });
    expect(useAppStore.getState().getConnection('C')?.connectionState).toBe('reconnecting');
    useAppStore.getState().retryServers();
    expect(down.calls).toMatchObject({ retryNow: 1, connect: 1 });
    await down.reconnect();
    expect(useAppStore.getState().getConnection('C')?.connectionState).toBe('connected');
  });
});
