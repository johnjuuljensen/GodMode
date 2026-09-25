// @vitest-environment jsdom
/**
 * Reconnect and resume (#171): after a lost connection comes back, the store catches up (projects,
 * attention) and resumes every open subscription from its own offset, once. Waking the page retries
 * at once. Drives the real store, and the components that open subscriptions, through fake hubs.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeHub, project, root, connectServers, flush, line } from '../test/fakeHub';
import { render, type Rendered } from '../test/render';
import { ProjectView } from '../components/Project/ProjectView';
import { TileGrid } from '../components/Tiles/TileGrid';
import { SidebarHeader } from '../components/Sidebar/Sidebar';
import { useAppStore } from './index';
import { projectKey } from './projectKey';

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
  isMaui: false,
  clearApiKey: () => {},
}));

/** The server's answer to the project's newest subscription: the lines replayed, then replay complete at the last. */
function replay(hub: FakeHub, projectId: string, fromOffset: number, offsets: number[]) {
  hub.lastReplay(projectId).answer(fromOffset, offsets);
}

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered | undefined;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([
    project('p1', 'first', 'Running', '2026-09-24T10:00:00Z'),
    project('p2', 'second', 'Running', '2026-09-24T09:00:00Z'),
  ], [root]);
  await connectServers({ A: hub });
});

afterEach(() => { view?.unmount(); view = undefined; });

describe('after a disconnect and reconnect', () => {
  beforeEach(async () => {
    useAppStore.getState().selectProject('A', 'p1');
    await useAppStore.getState().subscribeOutput('A', 'p1');
    replay(hub, 'p1', 0, [10, 20]);
    hub.resetCalls();
  });

  it('lists the projects again, so a state that changed meanwhile shows', async () => {
    hub.projects = [project('p1', 'first', 'WaitingInput', '2026-09-24T11:00:00Z'), hub.projects[1]];
    await hub.drop();
    await hub.reconnect();
    expect(hub.calls.listProjects).toBe(1);
    expect(useAppStore.getState().getConnection('A')?.projects[0].State).toBe('WaitingInput');
  });

  it('takes the attention list again', async () => {
    await hub.drop();
    await hub.reconnect();
    expect(hub.calls.getAttention).toBe(1);
  });

  it('resumes the open transcript from its offset, adding only what is new', async () => {
    await hub.drop();
    await hub.reconnect();
    expect(hub.subscriptions).toEqual([{ projectId: 'p1', fromOffset: 20 }]);
    replay(hub, 'p1', 20, [30]);
    expect(useAppStore.getState().outputMessages).toHaveLength(3);
    expect(useAppStore.getState().transcripts[projectKey('A', 'p1')]).toMatchObject({ offset: 30, phase: 'live' });
  });

  it('does not resume a transcript that was closed', async () => {
    await useAppStore.getState().unsubscribeOutput('A', 'p1');
    hub.resetCalls();
    await hub.drop();
    await hub.reconnect();
    expect(hub.subscriptions).toEqual([]);
  });

  it('keeps the server and its projects in the sidebar while reconnecting', async () => {
    await hub.drop();
    const s = useAppStore.getState();
    expect(s.profileGroups.flatMap(g => g.rootGroups.flatMap(rg => rg.items)).map(i => i.project.Id)).toEqual(['p1', 'p2']);
    expect(s.inactiveServers).toEqual([]);
  });
});

describe('the selected project and a tile, open over a reconnect', () => {
  it('each resumes from its own offset, once', async () => {
    useAppStore.getState().setTileView(true);
    await useAppStore.getState().subscribeTail('A', 'p2', 2);
    replay(hub, 'p2', 100, [110, 120]);
    hub.callbacks.onOutputReceived?.('p2', line(130));
    useAppStore.getState().selectProject('A', 'p1');
    await useAppStore.getState().subscribeOutput('A', 'p1');
    replay(hub, 'p1', 0, [10, 20]);
    hub.resetCalls();

    await hub.drop();
    await hub.reconnect();
    expect(hub.subscriptions).toEqual([{ projectId: 'p1', fromOffset: 20 }, { projectId: 'p2', fromOffset: 130 }]);
    replay(hub, 'p2', 130, [140]);
    expect(useAppStore.getState().tileMessages[projectKey('A', 'p2')]).toHaveLength(4);
  });
});

describe('rendered', () => {
  it('an open ProjectView is resubscribed once over a reconnect, from its offset', async () => {
    useAppStore.getState().selectProject('A', 'p1');
    view = await render(<ProjectView serverId="A" projectId="p1" />);
    replay(hub, 'p1', 0, [10, 20]);
    hub.resetCalls();
    await hub.drop();
    await hub.reconnect();
    await flush();
    expect(hub.subscriptions).toEqual([{ projectId: 'p1', fromOffset: 20 }]);
  });

  it('the tile grid is resubscribed once over a reconnect, each tile from its offset, keeping its lines', async () => {
    useAppStore.getState().setTileView(true);
    view = await render(<TileGrid />);
    expect(hub.subscriptions).toEqual([{ projectId: 'p1', fromOffset: -2 }, { projectId: 'p2', fromOffset: -2 }]);
    replay(hub, 'p1', 5, [10, 20]);
    replay(hub, 'p2', 50, [60]);
    hub.resetCalls();
    await hub.drop();
    await hub.reconnect();
    await flush();
    expect(hub.subscriptions).toEqual([{ projectId: 'p1', fromOffset: 20 }, { projectId: 'p2', fromOffset: 60 }]);
    expect(useAppStore.getState().tileMessages[projectKey('A', 'p1')]).toHaveLength(2);
  });
});

describe('waking the page', () => {
  let other: FakeHub;

  beforeEach(async () => {
    useAppStore.setState(initialState, true);
    await useAppStore.getState().loadServers();
    other = new FakeHub([], [root]);
    other.failConnect = true;
    await connectServers({ A: hub, B: other });
    hub.resetCalls();
    other.resetCalls();
    other.failConnect = false;
  });

  const setVisibility = (state: DocumentVisibilityState) =>
    Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });

  it('visible retries a reconnecting server at once, and connects one that failed to', async () => {
    await hub.drop();
    setVisibility('visible');
    document.dispatchEvent(new Event('visibilitychange'));
    await flush();
    expect(hub.calls.retryNow).toBe(1);
    expect(other.calls.connect).toBe(1);
    expect(useAppStore.getState().getConnection('B')?.connectionState).toBe('connected');
  });

  it('online does the same', async () => {
    await hub.drop();
    window.dispatchEvent(new Event('online'));
    await flush();
    expect(hub.calls.retryNow).toBe(1);
    expect(other.calls.connect).toBe(1);
  });

  it('hidden does nothing, and a connected server is left alone', async () => {
    setVisibility('hidden');
    document.dispatchEvent(new Event('visibilitychange'));
    await flush();
    expect(other.calls.connect).toBe(0);
    setVisibility('visible');
    document.dispatchEvent(new Event('visibilitychange'));
    await flush();
    expect(hub.calls).toMatchObject({ retryNow: 0, connect: 0 });
  });
});

describe('the connection indicator', () => {
  it('shows a lost server beside the title while it is retried, retries on a tap, and goes when it is back', async () => {
    view = await render(<SidebarHeader />);
    const indicator = () => view!.container.querySelector<HTMLButtonElement>('.connection-indicator-item');
    expect(indicator()).toBeNull();
    await act(() => hub.drop());
    expect(indicator()?.textContent).toBe('Server A');
    await act(async () => indicator()!.click());
    expect(hub.calls.retryNow).toBe(1);
    await act(() => hub.reconnect());
    expect(indicator()).toBeNull();
  });
});
