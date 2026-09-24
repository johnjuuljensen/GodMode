/**
 * The attention slice: what needs the user across every connected server, oldest first, one item
 * per ProjectKey (#172). Drives the real store through fake hubs.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { HubCallbacks } from '../signalr/hub';
import type { AttentionItem, AttentionKind, ServerInfo } from '../signalr/types';
import { useAppStore, type ServerConnection } from './index';

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

class FakeHub {
  callbacks: HubCallbacks = {};
  attention: AttentionItem[];
  constructor(attention: AttentionItem[]) { this.attention = attention; }
  setCallbacks(callbacks: HubCallbacks) { this.callbacks = callbacks; }
  async connect() { this.callbacks.onStateChanged?.('connected'); }
  async disconnect() {}
  async listProjects() { return []; }
  async listProjectRoots() { return []; }
  async listProfiles() { return []; }
  async getAttention() { return this.attention; }
}

const item = (projectId: string, since: string, kind: AttentionKind = 'Question'): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: since, Text: `${projectId} needs you`,
});

/** The merged list as `serverId:projectId@Since`, in order. */
const listed = () => useAppStore.getState().attention.map(i => `${i.serverId}:${i.ProjectId}@${i.Since.slice(11, 16)}`);

const initialState = useAppStore.getState();
let hubA: FakeHub;
let hubB: FakeHub;

async function connect(a: AttentionItem[], b: AttentionItem[]) {
  hubA = new FakeHub(a);
  hubB = new FakeHub(b);
  const conn = (id: string, hub: FakeHub): ServerConnection => ({
    serverInfo: { Id: id, Name: `Server ${id}`, Type: 'local', State: 'Running' } as ServerInfo,
    hub: hub as unknown as ServerConnection['hub'],
    connectionState: 'disconnected', projects: [], roots: [], profiles: [],
  });
  useAppStore.setState({ serverConnections: [conn('A', hubA), conn('B', hubB)] });
  await useAppStore.getState().connectServer('A');
  await useAppStore.getState().connectServer('B');
  // getAttention resolves after connect: let its then() run
  await new Promise(r => setTimeout(r, 0));
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
});

describe('attention', () => {
  it('(a) keeps two servers\' items for the same project ID apart, keyed by serverId:projectId', async () => {
    await connect([item('p1', '2026-09-24T10:00:00Z')], [item('p1', '2026-09-24T09:00:00Z')]);
    expect(listed()).toEqual(['B:p1@09:00', 'A:p1@10:00']);
  });

  it('(b) drops an item a server\'s AttentionChanged no longer has, and leaves the other server\'s', async () => {
    await connect(
      [item('p1', '2026-09-24T10:00:00Z'), item('p2', '2026-09-24T11:00:00Z')],
      [item('p1', '2026-09-24T09:00:00Z')],
    );
    hubA.callbacks.onAttentionChanged?.([item('p2', '2026-09-24T11:00:00Z')]);
    expect(listed()).toEqual(['B:p1@09:00', 'A:p2@11:00']);
  });

  it('(c) sorts items that arrive out of order by Since, oldest first, across servers', async () => {
    await connect(
      [item('p3', '2026-09-24T12:00:00Z'), item('p1', '2026-09-24T08:00:00Z')],
      [item('p2', '2026-09-24T10:00:00Z')],
    );
    hubB.callbacks.onAttentionChanged?.([item('p4', '2026-09-24T13:00:00Z'), item('p2', '2026-09-24T07:00:00Z')]);
    expect(listed()).toEqual(['B:p2@07:00', 'A:p1@08:00', 'A:p3@12:00', 'B:p4@13:00']);
  });

  it('(d) a disconnect of one server drops its items and keeps the other\'s', async () => {
    await connect([item('p1', '2026-09-24T10:00:00Z')], [item('p1', '2026-09-24T09:00:00Z')]);
    hubA.callbacks.onStateChanged?.('disconnected');
    expect(listed()).toEqual(['B:p1@09:00']);
  });

  it('keeps one item per ProjectKey when a server lists a project twice', async () => {
    await connect([item('p1', '2026-09-24T10:00:00Z'), item('p1', '2026-09-24T11:00:00Z', 'Finished')], []);
    expect(listed()).toEqual(['A:p1@11:00']);
  });

});
