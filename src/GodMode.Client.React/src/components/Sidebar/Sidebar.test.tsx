// @vitest-environment jsdom
/**
 * The left list under its profiles (#308): a root's + opens Create project on that root and its server,
 * and the group-by cycles through what is under each profile. Add server is in the gear menu, not the
 * header (#311). The header spells out no "GodMode": the window's title bar does (#313). Renders the Shell on
 * the real store.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRootInfo } from '../../signalr/types';
import { FakeHub, connectServers, project, status } from '../../test/fakeHub';
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

// The window's (max-width: 768px), which a test sets before rendering; every other query is as before
let phone = false;
const otherMedia = window.matchMedia;
window.matchMedia = (query: string) => query !== '(max-width: 768px)' ? otherMedia(query) : ({
  matches: phone, media: query, onchange: null,
  addEventListener: () => {}, removeEventListener: () => {},
  addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
}) as unknown as MediaQueryList;

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
  phone = false;
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
    expect(useAppStore.getState().activePage).toEqual({ type: 'createProject', context: { serverId, profileName: 'Default', rootName } });
    expect(view!.container.querySelector('.selected-root-name')?.textContent).toBe(rootName);
    expect(view!.container.querySelector('.selected-root-server')?.textContent).toBe(shownServer);
  });
});

describe("a root's + in a profile of its own", () => {
  it('passes the profile with the root name', async () => {
    const mega: ProjectRootInfo = { ...rootNamed('work'), ProfileName: 'Mega' };
    await connectServers({ A: new FakeHub([], [rootNamed('work'), mega]) });
    view = await render(<Shell />);

    const megaGroup = q('.profile-group').find(g => g.querySelector('.profile-group-name')?.textContent === 'Mega')!;
    await click(megaGroup.querySelector<HTMLButtonElement>('.root-action-btn')!);

    expect(useAppStore.getState().activePage).toEqual({ type: 'createProject', context: { serverId: 'A', profileName: 'Mega', rootName: 'work' } });
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

describe('the header (#313)', () => {
  it("has no title: the name is the title bar's", async () => {
    await connectServers({ A: new FakeHub([], [rootNamed('work')]) });
    view = await render(<Shell />);
    const header = view!.container.querySelector('.sidebar-header')!;
    expect(header.querySelector('.sidebar-title')).toBeNull();
    expect(header.textContent).not.toContain('GodMode');
  });
});

describe('Add server (#311)', () => {
  const gear = () => view!.container.querySelector<HTMLButtonElement>('.sidebar-settings-btn');
  const menuItem = (label: string) => q<HTMLButtonElement>('.sidebar-footer-menu-item').find(b => b.textContent === label);

  it('is not in the header, whose buttons are the everyday ones', async () => {
    await connectServers({ A: new FakeHub([], [rootNamed('work')]) });
    view = await render(<Shell />);
    expect(q('.sidebar-header button').map(b => b.getAttribute('title'))).toEqual(['Tile view', 'Create project']);
  });

  it.each([false, true])('is in the gear menu beside View Settings, and opens Add server closing the menu (phone: %s)', async isPhone => {
    phone = isPhone;
    await connectServers({ A: new FakeHub([], [rootNamed('work')]) });
    view = await render(<Shell />);
    // The phone's gear menu is under its list, the home's Projects tab
    if (isPhone) await click(q('.home-tab').find(b => b.textContent === 'Projects')!);

    await click(gear()!);
    expect(q('.sidebar-footer-menu-item').map(b => b.textContent)).toEqual(['View Settings', 'Add server']);

    await click(menuItem('Add server')!);
    expect(useAppStore.getState().activePage).toEqual({ type: 'addServer' });
    expect(view!.container.querySelector('.page-body h2')?.textContent).toBe('Add Server');
    expect(view!.container.querySelector('.sidebar-footer-menu')).toBeNull();
  });

  it('is still offered by the empty list, the first-run path', async () => {
    view = await render(<Shell />);
    expect(view!.container.querySelector('.sidebar-empty p')?.textContent).toBe('No servers configured');
    await click(view!.container.querySelector<HTMLButtonElement>('.sidebar-empty .btn-primary')!);
    expect(useAppStore.getState().activePage).toEqual({ type: 'addServer' });
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

describe("a session's kind", () => {
  it('is a label on its row, from the list and from a status pushed later', async () => {
    const hub = new FakeHub([{ ...project('Default/work/260929-feat-left-list-k7q2', 'Left list', 'Running', new Date().toISOString()), Kind: 'feat' }], [rootNamed('work')]);
    await connectServers({ A: hub });
    view = await render(<Shell />);

    const row = (name: string) => q('.project-item').find(r => r.querySelector('.project-name')?.textContent === name);
    expect(row('Left list')?.querySelector('.kind-label')?.textContent).toBe('feat');

    hub.callbacks.onProjectCreated?.({ ...status('Default/work/260929-bug-crash-abcd', 'Running'), Name: 'Crash', Kind: 'bug' });
    await vi.waitFor(() => expect(row('Crash')?.querySelector('.kind-label')?.textContent).toBe('bug'));
  });
});
