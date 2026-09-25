/**
 * Two servers whose project IDs overlap: every grouping, selection and per-project map must keep
 * them apart (#169). Drives the real store through fake hubs.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { parseClaudeMessage } from '../signalr/parseMessage';
import { FakeHub, project, root, status, connectServers } from '../test/fakeHub';
import { useAppStore, type SidebarGroupBy } from './index';
import { projectKey } from './projectKey';

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const DISMISSED_KEY = 'godmode-dismissed-projects-v2';

const question = parseClaudeMessage(JSON.stringify({
  type: 'assistant', message: { content: [{ type: 'text', text: 'Shall I continue?' }] },
}));

const initialState = useAppStore.getState();
let hubA: FakeHub;
let hubB: FakeHub;

/** Server A has p1 (Running) and p2; server B has p1 (WaitingInput, the most recent) and p3. */
async function connectTwoServers() {
  hubA = new FakeHub([
    project('p1', 'shared', 'Running', '2026-09-24T10:00:00Z'),
    project('p2', 'only-a', 'Idle', '2026-09-24T09:00:00Z'),
  ], [root]);
  hubB = new FakeHub([
    project('p1', 'shared', 'WaitingInput', '2026-09-24T11:00:00Z'),
    project('p3', 'only-b', 'Idle', '2026-09-24T08:00:00Z'),
  ], [root]);
  await connectServers({ A: hubA, B: hubB });
}

function groupBy(g: SidebarGroupBy) {
  useAppStore.setState({ sidebarGroupBy: g });
  useAppStore.getState().setProfileFilter('All');
}

/** Every entry the sidebar shows, as the (serverId, projectId) a click selects. */
const sidebarEntries = () => useAppStore.getState().profileGroups
  .flatMap(g => g.rootGroups.flatMap(rg => rg.items))
  .map(i => ({ serverId: i.serverId, projectId: i.project.Id, project: i.project, label: i.serverLabel }));

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  await connectTwoServers();
});

describe('grouping', () => {
  it.each<SidebarGroupBy>(['profile', 'root', 'recent', 'status'])('by %s keeps each project on its own server', g => {
    groupBy(g);
    const entries = sidebarEntries().map(e => `${e.serverId}:${e.projectId}`).sort();
    expect(entries).toEqual(['A:p1', 'A:p2', 'B:p1', 'B:p3']);
  });

  it.each<SidebarGroupBy>(['profile', 'root', 'recent', 'status'])('by %s opens the project shown, on its server', g => {
    groupBy(g);
    for (const e of sidebarEntries()) {
      useAppStore.getState().selectProject(e.serverId, e.projectId);
      const { serverId, projectId } = useAppStore.getState().selectedProject!;
      const opened = useAppStore.getState().getConnection(serverId)?.projects.find(p => p.Id === projectId);
      expect(opened).toBe(e.project);
    }
  });

  it('labels a name shown from two servers with its server', () => {
    groupBy('profile');
    const labels = Object.fromEntries(sidebarEntries().map(e => [`${e.serverId}:${e.projectId}`, e.label]));
    expect(labels).toEqual({ 'A:p1': 'Server A', 'B:p1': 'Server B', 'A:p2': undefined, 'B:p3': undefined });
  });
});

describe('per-project state is per server', () => {
  it('dismissing A:p1 leaves B:p1 waiting', () => {
    useAppStore.getState().selectProject('A', 'p1');
    useAppStore.getState().dismissQuestion();
    const s = useAppStore.getState();
    expect(s.dismissedProjects[projectKey('A', 'p1')]).toBe(true);
    expect(s.dismissedProjects[projectKey('B', 'p1')]).toBeUndefined();
    expect(s.totalWaitingCount).toBe(1);
  });

  it("a question in A:p1's output marks A:p1, not B:p1", () => {
    // B:p1 was listed WaitingInput, so asks; running, it no longer does
    hubB.callbacks.onStatusChanged?.('p1', status('p1', 'Running'));
    hubA.callbacks.onOutputReceived?.('p1', { offset: 10, message: question });
    const pq = useAppStore.getState().projectQuestions;
    expect(pq[projectKey('A', 'p1')]).toBe(true);
    expect(pq[projectKey('B', 'p1')]).toBe(false);
  });

  it("A:p1's tile output does not reach B:p1's tile", async () => {
    useAppStore.getState().setTileView(true);
    await useAppStore.getState().subscribeTail('A', 'p1', 2);
    await useAppStore.getState().subscribeTail('B', 'p1', 2);
    hubA.lastReplay('p1').batch(0, [{ offset: 10, message: question }]);
    hubA.lastReplay('p1').complete(10);
    const s = useAppStore.getState();
    expect(s.tileMessages[projectKey('A', 'p1')]).toHaveLength(1);
    expect(s.tileMessages[projectKey('B', 'p1')]).toEqual([]);
    expect(s.tileLoading[projectKey('A', 'p1')]).toBe(false);
    expect(s.tileLoading[projectKey('B', 'p1')]).toBe(true);
  });
});

describe('dismissedProjects', () => {
  it('is pruned on refresh when the project is gone, on that server only', async () => {
    useAppStore.getState().selectProject('A', 'p1');
    useAppStore.getState().dismissQuestion();
    useAppStore.getState().selectProject('B', 'p3');
    useAppStore.getState().dismissQuestion();

    hubA.projects = hubA.projects.filter(p => p.Id !== 'p1');
    await useAppStore.getState().refreshProjects('A');

    const dp = useAppStore.getState().dismissedProjects;
    expect(Object.keys(dp)).toEqual([projectKey('B', 'p3')]);
    expect(Object.keys(JSON.parse(localStorage.getItem(DISMISSED_KEY) ?? '{}'))).toEqual([projectKey('B', 'p3')]);
  });

  it('is written once when a Running event clears it, not on every Running event', () => {
    useAppStore.getState().selectProject('A', 'p1');
    useAppStore.getState().dismissQuestion();
    const setItem = vi.spyOn(localStorage, 'setItem');
    for (let i = 0; i < 3; i++) hubA.callbacks.onStatusChanged?.('p1', status('p1', 'Running'));
    hubB.callbacks.onStatusChanged?.('p3', status('p3', 'Running'));
    expect(setItem.mock.calls.filter(([k]) => k === DISMISSED_KEY)).toHaveLength(1);
    expect(useAppStore.getState().dismissedProjects).toEqual({});
  });
});

describe('a project created elsewhere (#170)', () => {
  const created = status('p9', 'Running');

  it('is listed without moving the selection', () => {
    useAppStore.getState().selectProject('B', 'p3');
    const before = useAppStore.getState();
    hubA.callbacks.onProjectCreated?.(created);
    const s = useAppStore.getState();
    expect(s.selectedProject).toEqual({ serverId: 'B', projectId: 'p3' });
    expect(s.outputMessages).toBe(before.outputMessages);
    expect(s.question).toBe(before.question);
    expect(s.getConnection('A')?.projects.map(p => p.Id)).toEqual(['p1', 'p2', 'p9']);
  });

  it('leaves the create page open', () => {
    useAppStore.getState().setActivePage({ type: 'createProject', context: { serverId: 'B', rootName: 'work' } });
    hubA.callbacks.onProjectCreated?.(created);
    const s = useAppStore.getState();
    expect(s.activePage).toEqual({ type: 'createProject', context: { serverId: 'B', rootName: 'work' } });
    expect(s.selectedProject).toBeNull();
  });
});

describe('a project this client created (#170)', () => {
  const created = status('p9', 'Running');

  it.each([
    ['before', true],
    ['after', false],
  ])('opens once its own call returns, with the broadcast arriving %s', (_when, broadcastFirst) => {
    useAppStore.getState().setActivePage({ type: 'createProject', context: { serverId: 'A', rootName: 'work' } });
    if (broadcastFirst) hubA.callbacks.onProjectCreated?.(created);
    useAppStore.getState().openCreatedProject('A', created);
    if (!broadcastFirst) hubA.callbacks.onProjectCreated?.(created);
    const s = useAppStore.getState();
    expect(s.selectedProject).toEqual({ serverId: 'A', projectId: 'p9' });
    expect(s.activePage).toBeNull();
    expect(s.getConnection('A')?.projects.map(p => p.Id)).toEqual(['p1', 'p2', 'p9']);
  });
});
