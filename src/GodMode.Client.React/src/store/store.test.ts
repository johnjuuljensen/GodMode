/**
 * Two servers whose project IDs overlap: every grouping, selection and per-project map must keep
 * them apart (#169). Drives the real store through fake hubs.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { HubCallbacks } from '../signalr/hub';
import type {
  ProjectSummary, ProjectRootInfo, ProfileInfo, ProjectState, ProjectStatus, ServerInfo,
} from '../signalr/types';
import { parseClaudeMessage } from '../signalr/parseMessage';
import { useAppStore, type ServerConnection, type SidebarGroupBy } from './index';
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

class FakeHub {
  callbacks: HubCallbacks = {};
  projects: ProjectSummary[];
  roots: ProjectRootInfo[];
  constructor(projects: ProjectSummary[], roots: ProjectRootInfo[]) {
    this.projects = projects;
    this.roots = roots;
  }
  setCallbacks(callbacks: HubCallbacks) { this.callbacks = callbacks; }
  async connect() { this.callbacks.onStateChanged?.('connected'); }
  async disconnect() {}
  async listProjects() { return this.projects; }
  async listProjectRoots() { return this.roots; }
  async listProfiles(): Promise<ProfileInfo[]> { return []; }
  async getAttention() { return []; }
  async subscribeProject() {}
  async unsubscribeProject() {}
}

const project = (id: string, name: string, state: ProjectState, updatedAt: string): ProjectSummary => ({
  Id: id, Name: name, State: state, UpdatedAt: updatedAt, RootName: 'work', ProfileName: 'Default',
});
const root: ProjectRootInfo = { Name: 'work', ProfileName: 'Default', Actions: [] } as unknown as ProjectRootInfo;
const status = (id: string, state: ProjectState) =>
  ({ Id: id, Name: id, State: state, UpdatedAt: '2026-09-24T12:00:00Z' }) as ProjectStatus;
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
  const conn = (id: string, hub: FakeHub): ServerConnection => ({
    serverInfo: { Id: id, Name: `Server ${id}`, Type: 'local', State: 'Running' } as ServerInfo,
    hub: hub as unknown as ServerConnection['hub'],
    connectionState: 'disconnected', projects: [], roots: [], profiles: [],
  });
  useAppStore.setState({ serverConnections: [conn('A', hubA), conn('B', hubB)] });
  await useAppStore.getState().connectServer('A');
  await useAppStore.getState().connectServer('B');
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
    hubA.callbacks.onOutputReceived?.('p1', { offset: 10, message: question });
    const pq = useAppStore.getState().projectQuestions;
    expect(pq[projectKey('A', 'p1')]).toBe(true);
    expect(pq[projectKey('B', 'p1')]).toBeUndefined();
  });

  it("A:p1's tile output does not reach B:p1's tile", async () => {
    useAppStore.getState().setTileView(true);
    await useAppStore.getState().subscribeTail('A', 'p1', 2);
    await useAppStore.getState().subscribeTail('B', 'p1', 2);
    hubA.callbacks.onOutputBatch?.('p1', 0, [{ offset: 10, message: question }]);
    hubA.callbacks.onOutputReplayComplete?.('p1', 10);
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
