// @vitest-environment jsdom
/**
 * The left list under its profiles (#308): a root's + opens Create project on that root and its server,
 * and the group-by cycles through what is under each profile. Renders the Shell on the real store.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRootInfo } from '../../signalr/types';
import { FakeHub, connectServers } from '../../test/fakeHub';
import { render, click, type Rendered } from '../../test/render';
import { useAppStore, loadGroupBy } from '../../store';
import { Shell } from '../Shell';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const rootNamed = (name: string): ProjectRootInfo => ({
  Name: name, ProfileName: 'Default',
  Actions: [{ Name: 'issue', AllowSkipPermissions: false, InputSchema: { type: 'object', properties: { title: { type: 'string', title: 'Title' } } } }],
});

const initialState = useAppStore.getState();
let view: Rendered | undefined;

const q = <T extends Element = HTMLElement>(sel: string) => [...view!.container.querySelectorAll<T>(sel)];
/** The + of the root header named `name`. */
const plusOf = (name: string) => q('.root-group-header')
  .find(h => h.querySelector('.root-group-name')?.textContent === name)!.querySelector<HTMLButtonElement>('.root-action-btn');

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  sessionStorage.clear();
});

afterEach(() => { view?.unmount(); view = undefined; });

describe("a root's +", () => {
  beforeEach(async () => {
    await connectServers({ A: new FakeHub([], [rootNamed('work'), rootNamed('play')]), B: new FakeHub([], [rootNamed('work')]) });
    view = await render(<Shell />);
  });

  it('is on every root header, qualified by server where two servers share a root name', () => {
    expect(q('.profile-group-name').map(e => e.textContent)).toEqual(['Default']);
    expect(q('.root-group-name').map(e => e.textContent)).toEqual(['play', 'work (Server A)', 'work (Server B)']);
    expect(q('.root-action-btn')).toHaveLength(3);
  });

  it.each([
    ['work (Server B)', 'B', 'work', 'on Server B'],
    ['play', 'A', 'play', 'on Server A'],
  ])('%s opens Create project on that server and root', async (header, serverId, rootName, shownServer) => {
    await click(plusOf(header)!);
    expect(useAppStore.getState().activePage).toEqual({ type: 'createProject', context: { serverId, rootName } });
    expect(view!.container.querySelector('.selected-root-name')?.textContent).toBe(rootName);
    expect(view!.container.querySelector('.selected-root-server')?.textContent).toBe(shownServer);
  });
});

describe('the group-by', () => {
  beforeEach(async () => {
    await connectServers({ A: new FakeHub([], [rootNamed('work')]) });
    view = await render(<Shell />);
  });

  it('cycles Root, Recent, Status, keeping the profile header in each', async () => {
    const bar = () => view!.container.querySelector<HTMLButtonElement>('.sidebar-sort-bar')!;
    const seen: string[] = [];
    for (let i = 0; i < 4; i++) {
      seen.push(`${bar().textContent}: ${q('.profile-group-name').map(e => e.textContent).join()}, ${q('.root-group-name').length} root headers`);
      await click(bar());
    }
    expect(seen).toEqual([
      'Root: Default, 1 root headers',
      'Recent: Default, 0 root headers',
      'Status: Default, 0 root headers',
      'Root: Default, 1 root headers',
    ]);
  });
});

describe('the stored group-by', () => {
  it.each(['root', 'recent', 'status'])('%s is kept', v => {
    localStorage.setItem('godmode-sidebar-groupby', v);
    expect(loadGroupBy()).toBe(v);
  });

  it.each(['profile', 'nonsense'])('%s, no longer a mode, falls back to Root', v => {
    localStorage.setItem('godmode-sidebar-groupby', v);
    expect(loadGroupBy()).toBe('root');
  });

  it('none gives Root', () => {
    expect(loadGroupBy()).toBe('root');
  });
});
