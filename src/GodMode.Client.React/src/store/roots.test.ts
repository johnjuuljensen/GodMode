// @vitest-environment jsdom
/**
 * Live roots (#323): a server's RootsChanged replaces its roots and profiles in the store, and the left
 * list follows without a reconnect. A project whose root the server no longer lists stays in the list,
 * under its root's name, with no +. Drives the real store through a fake hub.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeHub, project, root, connectServers } from '../test/fakeHub';
import type { ProjectRootInfo } from '../signalr/types';
import { useAppStore } from './index';

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const store = () => useAppStore.getState();
const initialState = useAppStore.getState();
const fresh: ProjectRootInfo = { Name: 'fresh', ProfileName: 'Private', Actions: [{ Name: 'Create', AllowSkipPermissions: false, Session: true, Transient: false }] };

/** The left list, as `profile`, `  root [+]` and `    project` lines. */
const outline = () => store().profileGroups.flatMap(g => [
  g.name,
  ...g.rootGroups.flatMap(rg => [`  ${rg.name}${rg.canCreate ? ' +' : ''}`, ...rg.items.map(i => `    ${i.project.Name}`)]),
]);

let hub: FakeHub;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([project('p1', 'first', 'Running', '2026-09-24T10:00:00Z')], [root]);
  await connectServers({ A: hub });
});

describe('RootsChanged', () => {
  it('replaces the server\'s roots and profiles, and the list shows a new root with its +', () => {
    hub.callbacks.onRootsChanged?.([root, fresh], [{ Name: 'Default' }, { Name: 'Private', Description: 'mine' }]);

    const conn = store().getConnection('A')!;
    expect(conn.roots.map(r => r.Name)).toEqual(['work', 'fresh']);
    expect(conn.profiles).toEqual([{ Name: 'Default' }, { Name: 'Private', Description: 'mine' }]);
    expect(outline()).toEqual(['Default', '  work', '    first', 'Private', '  fresh +']);
    expect(store().profileFilterOptions).toContain('Private');
  });

  it('keeps a project whose root is no longer listed, under its root, with no +', () => {
    hub.callbacks.onRootsChanged?.([fresh], [{ Name: 'Private' }]);

    expect(outline()).toEqual(['Default', '  work', '    first', 'Private', '  fresh +']);

    // Once the server lets it go, the project leaves, and its root with it
    hub.callbacks.onProjectDeleted?.('p1');
    expect(outline()).toEqual(['Private', '  fresh +']);
  });
});
