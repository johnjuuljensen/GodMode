// @vitest-environment jsdom
/**
 * The left list folds above a session too (#427): a profile's header, a root's, and the inactive servers'
 * section. A folded header says how many are under it, and shows a dot when any of them needs the user. Folds
 * are kept on this device by keys that stay the same whatever other servers are listed, pruned of what is no
 * longer listed. A session selected under a fold is shown, its folds opened; an open parent folds its quiet
 * children under "N older" (#397). Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, ProjectRootInfo, ProjectSummary, ProjectState } from '../../signalr/types';
import { FakeHub, connectServers, project } from '../../test/fakeHub';
import { render, click, type Rendered } from '../../test/render';
import { useAppStore, loadKeySet, profileFoldKey, rootFoldKey, INACTIVE_FOLD_KEY } from '../../store';
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

let phone = false;
const otherMedia = window.matchMedia;
window.matchMedia = (query: string) => query !== '(max-width: 768px)' ? otherMedia(query) : ({
  matches: phone, media: query, onchange: null,
  addEventListener: () => {}, removeEventListener: () => {},
  addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
}) as unknown as MediaQueryList;

const rootOf = (name: string, profile = 'Default'): ProjectRootInfo => ({ Name: name, ProfileName: profile, Actions: [] });

const now = new Date().toISOString();
const longAgo = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString();
/** A session in `root` of `profile`, started by `parent` when one is given. */
const session = (name: string, root: string, parent?: string, profile = 'Default', state: ProjectState = 'Idle', at = now): ProjectSummary =>
  ({ ...project(`${profile}/${root}/${name}`, name, state, at), RootName: root, ProfileName: profile, ParentId: parent ?? null });

const HEADERS_KEY = 'godmode-folded-headers';
const COLLAPSED_KEY = 'godmode-collapsed-sessions';

const initialState = useAppStore.getState();
let view: Rendered | undefined;
let hub: FakeHub;

const q = <T extends Element = HTMLElement>(sel: string, from: ParentNode = view!.container) => [...from.querySelectorAll<T>(sel)];
const names = () => q('.project-name').map(e => e.textContent);

const profileHeader = (name: string) => q('.profile-group-header').find(h => h.querySelector('.profile-group-name')?.textContent === name)!;
const rootHeader = (name: string) => q('.root-group-header').find(h => h.querySelector('.root-group-name')?.textContent === name)!;
const foldOf = (header: Element) => header.querySelector<HTMLButtonElement>('.group-fold-toggle');
const toggleOf = (name: string) => q('.project-item').find(r => r.querySelector('.project-name')?.textContent === name)!
  .querySelector<HTMLButtonElement>('.project-children-toggle');
const stored = (key: string) => JSON.parse(localStorage.getItem(key) ?? '{}');

const needs = (projectId: string): AttentionItem =>
  ({ ProjectId: projectId, ProjectName: projectId, Profile: 'Default', Root: 'fleet', Kind: 'Question', Since: now, Text: 'Go on?' }) as unknown as AttentionItem;

beforeEach(() => {
  useAppStore.setState(initialState, true);
  useAppStore.setState({ collapsedSessions: {}, foldedHeaders: {} });
  localStorage.clear();
  history.replaceState(null, '', '#/');
  sessionStorage.clear();
  phone = false;
});

afterEach(() => { view?.unmount(); view = undefined; });

/** A fleet in Default (an overseer, its epic, two workers, one in another root) and a session in Mega. */
async function showFleet(more: Record<string, FakeHub> = {}) {
  hub = new FakeHub([
    session('overseer', 'fleet'),
    session('epic', 'fleet', 'Default/fleet/overseer'),
    session('worker-1', 'fleet', 'Default/fleet/epic'),
    session('worker-2', 'work', 'Default/fleet/epic'),
    session('notes', 'side', undefined, 'Mega'),
  ], [rootOf('fleet'), rootOf('work'), rootOf('side', 'Mega')]);
  await connectServers({ A: hub, ...more });
  view = await render(<Shell />);
  if (phone) await click(q<HTMLButtonElement>('.home-tab').find(b => b.textContent === 'Projects')!);
}

describe.each([false, true])('on a phone: %s', isPhone => {
  beforeEach(() => { phone = isPhone; });

  it("folds a profile's header to its count, its open-in-window button left as it is, and opens it again", async () => {
    useAppStore.setState({ canOpenWindows: true });
    await showFleet();
    await click(foldOf(profileHeader('Default'))!);
    expect(names()).toEqual(['notes']);
    expect(foldOf(profileHeader('Default'))!.getAttribute('aria-expanded')).toBe('false');
    expect(profileHeader('Default').querySelector('.profile-group-count')?.textContent).toBe('4');
    expect(profileHeader('Default').querySelector('.profile-window-btn')).not.toBeNull();

    await click(foldOf(profileHeader('Default'))!);
    expect(names()).toEqual(['overseer', 'epic', 'worker-1', 'worker-2', 'notes']);
  });

  it("folds a root's header to a count of every session under it, its + left as it is", async () => {
    hub = new FakeHub([session('a', 'work'), session('b', 'work', 'Default/work/a')],
      [{ ...rootOf('work'), Actions: [{ Name: 'freeform' }] } as unknown as ProjectRootInfo]);
    await connectServers({ A: hub });
    view = await render(<Shell />);
    if (phone) await click(q<HTMLButtonElement>('.home-tab').find(b => b.textContent === 'Projects')!);

    await click(foldOf(rootHeader('work'))!);
    expect(names()).toEqual([]);
    expect(rootHeader('work').querySelector('.group-fold-summary .profile-group-count')?.textContent).toBe('2');
    expect(rootHeader('work').querySelector('.root-action-btn')).not.toBeNull();
  });
});

it('shows a dot on a folded profile and a folded root when a session under them needs the user, and none otherwise', async () => {
  await showFleet();
  await click(foldOf(rootHeader('fleet'))!);
  expect(rootHeader('fleet').querySelector('.project-children-attention')).toBeNull();

  // A grandchild nested under the root's parent, from another root, is under it too
  await act(async () => hub.callbacks.onAttentionChanged?.([needs('Default/work/worker-2')]));
  expect(rootHeader('fleet').querySelector('.project-children-attention')).not.toBeNull();

  await click(foldOf(profileHeader('Default'))!);
  expect(profileHeader('Default').querySelector('.project-children-attention')).not.toBeNull();
  expect(profileHeader('Mega').querySelector('.project-children-attention')).toBeNull();
});

it('folds the inactive servers to their count', async () => {
  const off = new FakeHub([], []);
  off.failConnect = true;
  await showFleet({ B: off });
  const inactive = () => q('.inactive-section')[0];
  expect(q('.server-item', inactive())).toHaveLength(1);

  await click(foldOf(inactive())!);
  expect(q('.server-item', inactive())).toHaveLength(0);
  expect(inactive().querySelector('.profile-group-count')?.textContent).toBe('1');
  expect(stored(HEADERS_KEY)).toEqual({ [INACTIVE_FOLD_KEY]: true });
});

it('keeps folds on this device, a profile by its name and a root by its server, profile and name', async () => {
  await showFleet();
  await click(foldOf(rootHeader('fleet'))!);
  await click(foldOf(profileHeader('Mega'))!);
  expect(stored(HEADERS_KEY)).toEqual({ 'profile:mega': true, 'root:A:Default/fleet': true });
  expect(rootFoldKey('A', 'Default', 'fleet')).toBe('root:A:Default/fleet');
  expect(profileFoldKey('Mega')).toBe('profile:mega');
});

it('keeps a root folded when another server with a root of its name connects, and its shown name changes', async () => {
  await showFleet();
  await click(foldOf(rootHeader('fleet'))!);

  const other = new FakeHub([session('elsewhere', 'fleet')], [rootOf('fleet')]);
  await act(async () => { await connectServers({ A: hub, B: other }); });
  // Both are qualified by server now: A's stays folded, B's is open
  expect(foldOf(rootHeader('fleet (Server A)'))!.getAttribute('aria-expanded')).toBe('false');
  expect(foldOf(rootHeader('fleet (Server B)'))!.getAttribute('aria-expanded')).toBe('true');
  expect(names()).toContain('elsewhere');
  expect(names()).not.toContain('worker-1');
});

it('has no root folds when grouped by recent: the profile still folds', async () => {
  await showFleet();
  await act(async () => useAppStore.getState().cycleSidebarGroupBy());
  expect(useAppStore.getState().sidebarGroupBy).toBe('recent');
  expect(q('.root-group-header')).toHaveLength(0);

  await click(foldOf(profileHeader('Default'))!);
  expect(names()).toEqual(['notes']);
});

it("has no fold for its own profile in the profile's window, whatever was kept", async () => {
  localStorage.setItem(HEADERS_KEY, JSON.stringify({ 'profile:default': true }));
  useAppStore.setState({ foldedHeaders: { 'profile:default': true }, lockedProfile: 'Default', profileFilter: 'Default' });
  await showFleet();
  expect(foldOf(profileHeader('Default'))).toBeNull();
  expect(names()).toEqual(['overseer', 'epic', 'worker-1', 'worker-2']);
});

describe('a session selected under a fold', () => {
  it('opens its profile, its root and every parent above it, and keeps them open', async () => {
    await showFleet();
    await click(toggleOf('epic')!);
    await click(toggleOf('overseer')!);
    await click(foldOf(rootHeader('fleet'))!);
    await click(foldOf(profileHeader('Default'))!);
    await click(foldOf(profileHeader('Mega'))!);
    expect(names()).toEqual([]);

    // As the inbox, a notification or voice selects it
    await act(async () => useAppStore.getState().selectProject('A', 'Default/fleet/worker-1'));
    expect(names()).toEqual(['overseer', 'epic', 'worker-1', 'worker-2']);
    expect(q('.project-item.selected').map(r => r.querySelector('.project-name')?.textContent)).toEqual(['worker-1']);
    // Only its own folds open: Mega stays folded
    expect(stored(HEADERS_KEY)).toEqual({ 'profile:mega': true });
    expect(stored(COLLAPSED_KEY)).toEqual({});
  });

  it('leaves the folds alone when nothing hides it', async () => {
    await showFleet();
    await click(foldOf(profileHeader('Mega'))!);
    await click(toggleOf('epic')!);
    await act(async () => useAppStore.getState().selectProject('A', 'Default/fleet/overseer'));
    expect(stored(HEADERS_KEY)).toEqual({ 'profile:mega': true });
    expect(stored(COLLAPSED_KEY)).toEqual({ 'A:Default/fleet/epic': true });
  });
});

describe('quiet children of an open parent', () => {
  async function showOldFleet() {
    hub = new FakeHub([
      session('overseer', 'fleet'),
      session('live', 'fleet', 'Default/fleet/overseer'),
      session('done-1', 'fleet', 'Default/fleet/overseer', 'Default', 'Stopped', longAgo),
      session('done-2', 'fleet', 'Default/fleet/overseer', 'Default', 'Stopped', longAgo),
    ], [rootOf('fleet')]);
    await connectServers({ A: hub });
    view = await render(<Shell />);
  }
  const olderUnder = () => q<HTMLButtonElement>('.project-children > .project-list-older');

  it('fold under "N older" within the parent, one tap from shown', async () => {
    await showOldFleet();
    expect(names()).toEqual(['overseer', 'live']);
    expect(olderUnder().map(b => b.textContent)).toEqual(['2 older']);
    // The parent's count is still everything under it
    await click(olderUnder()[0]);
    expect(names()).toEqual(['overseer', 'live', 'done-1', 'done-2']);
    expect(olderUnder()[0].textContent).toBe('Hide older');
  });

  it('keep one that is selected, or needs the user, shown', async () => {
    await showOldFleet();
    await act(async () => useAppStore.getState().selectProject('A', 'Default/fleet/done-1'));
    await act(async () => hub.callbacks.onAttentionChanged?.([needs('Default/fleet/done-2')]));
    expect(names()).toEqual(['overseer', 'live', 'done-1', 'done-2']);
    expect(olderUnder()).toHaveLength(0);
  });
});

describe('what is kept', () => {
  it('is pruned of the roots and sessions a server no longer lists, once it has listed them', async () => {
    await showFleet();
    await click(foldOf(rootHeader('work'))!);
    await click(foldOf(rootHeader('fleet'))!);
    useAppStore.setState({ collapsedSessions: { ['A:Default/fleet/epic' as never]: true, ['A:Default/fleet/gone' as never]: true } });

    hub.roots = [rootOf('fleet'), rootOf('side', 'Mega')];
    hub.projects = hub.projects.filter(p => p.RootName !== 'work');
    await act(async () => useAppStore.getState().refreshProjects('A'));
    expect(Object.keys(useAppStore.getState().foldedHeaders)).toEqual(['root:A:Default/fleet']);
    expect(stored(HEADERS_KEY)).toEqual({ 'root:A:Default/fleet': true });
    expect(useAppStore.getState().collapsedSessions).toEqual({ 'A:Default/fleet/epic': true });
    expect(stored(COLLAPSED_KEY)).toEqual({ 'A:Default/fleet/epic': true });
  });

  it("keeps a profile's fold while a server that may have it has not listed, and prunes it once every one has", async () => {
    const off = new FakeHub([], []);
    off.failConnect = true;
    await showFleet({ B: off });
    useAppStore.setState({ foldedHeaders: { 'profile:mega': true, 'profile:gone': true } });

    await act(async () => useAppStore.getState().refreshProjects('A'));
    expect(useAppStore.getState().foldedHeaders).toEqual({ 'profile:mega': true, 'profile:gone': true });

    await act(async () => { await connectServers({ A: hub }); });
    await act(async () => useAppStore.getState().refreshProjects('A'));
    expect(useAppStore.getState().foldedHeaders).toEqual({ 'profile:mega': true });
  });
});

describe('a stored value that is not a set of keys', () => {
  it.each([
    ['null', 'null'], ['an array', '["a"]'], ['a string', '"a"'], ['a number', '1'], ['not JSON', '{oops'],
  ])('reads %s as none', (_, value) => {
    localStorage.setItem('k', value);
    expect(loadKeySet('k')).toEqual({});
  });

  it('drops an entry that is not true', () => {
    localStorage.setItem('k', '{"a": true, "b": 1, "c": null}');
    expect(loadKeySet('k')).toEqual({ a: true });
  });

  it('starts the store with nothing folded or collapsed, and a fold kept over it', async () => {
    localStorage.setItem(COLLAPSED_KEY, 'null');
    localStorage.setItem(HEADERS_KEY, 'null');
    vi.resetModules();
    const { useAppStore: fresh } = await import('../../store');
    expect(fresh.getState().collapsedSessions).toEqual({});
    expect(fresh.getState().foldedHeaders).toEqual({});
    fresh.getState().toggleCollapsed('A:x' as never);
    fresh.getState().toggleFoldedHeader('profile:x');
    expect(stored(COLLAPSED_KEY)).toEqual({ 'A:x': true });
    expect(stored(HEADERS_KEY)).toEqual({ 'profile:x': true });
  });
});

describe('on a phone, a row swiped open', () => {
  beforeEach(() => { phone = true; });

  async function swipe(el: HTMLElement, x0: number, x1: number, y = 10) {
    const touch = (type: string, x: number) => {
      const event = new Event(type, { bubbles: true, cancelable: true });
      Object.defineProperty(event, 'touches', { value: type === 'touchend' ? [] : [{ clientX: x, clientY: y }] });
      el.dispatchEvent(event);
    };
    await act(async () => {
      touch('touchstart', x0);
      for (let x = x0; x0 > x1 ? x >= x1 : x <= x1; x += x0 > x1 ? -10 : 10) touch('touchmove', x);
    });
    await act(async () => touch('touchend', x1));
  }

  it('closes on a tap on its toggle, rather than collapsing', async () => {
    await showFleet();
    const row = q('.project-item').find(r => r.querySelector('.project-name')?.textContent === 'epic')!;
    await swipe(row, 200, 80);
    expect(q('.project-item-swipe-delete')).toHaveLength(1);

    await click(toggleOf('epic')!);
    expect(q('.project-item-swipe-delete')).toHaveLength(0);
    expect(names()).toContain('worker-1');
    expect(useAppStore.getState().collapsedSessions).toEqual({});

    await click(toggleOf('epic')!);
    expect(names()).not.toContain('worker-1');
  });
});
