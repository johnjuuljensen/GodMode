// @vitest-environment jsdom
/**
 * The left list nests each session under the one that started it, on several levels (#390): a parent's row
 * collapses what is under it, showing how many and whether any of them needs the user. Profiles never mix: a
 * child of another profile's session is at the top of its own, noting its parent. Renders the Shell on the
 * real store, on a desktop and on a phone.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, ProjectRootInfo, ProjectSummary } from '../../signalr/types';
import { FakeHub, connectServers, project } from '../../test/fakeHub';
import { render, click, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
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
/** A session in `root` of `profile`, started by `parent` when one is given. */
const session = (name: string, root: string, parent?: string, profile = 'Default'): ProjectSummary =>
  ({ ...project(`${profile}/${root}/${name}`, name, 'Idle', now), RootName: root, ProfileName: profile, ParentId: parent ?? null });

const initialState = useAppStore.getState();
let view: Rendered | undefined;
let hub: FakeHub;

const q = <T extends Element = HTMLElement>(sel: string, from: ParentNode = view!.container) => [...from.querySelectorAll<T>(sel)];

/** The list as text: each row's name, two spaces in per level, with its own root in brackets. */
function outline(): string[] {
  const rows = (list: Element, depth: number): string[] => [...list.children].flatMap(el =>
    el.classList.contains('project-item-wrapper')
      ? [`${'  '.repeat(depth)}${el.querySelector('.project-name')!.textContent}${
        el.querySelector('.project-own-root') ? ` [${el.querySelector('.project-own-root')!.textContent}]` : ''}`]
      : el.classList.contains('project-children') ? rows(el, depth + 1) : []);
  return q('.project-list').flatMap(l => rows(l, 0));
}

const toggleOf = (name: string) => q('.project-item').find(r => r.querySelector('.project-name')?.textContent === name)!
  .querySelector<HTMLButtonElement>('.project-children-toggle');

const needs = (projectId: string): AttentionItem =>
  ({ ProjectId: projectId, ProjectName: projectId, Profile: 'Default', Root: 'fleet', Kind: 'Question', Since: now, Text: 'Go on?' }) as unknown as AttentionItem;

beforeEach(() => {
  useAppStore.setState(initialState, true);
  useAppStore.setState({ collapsedSessions: {} });
  localStorage.clear();
  history.replaceState(null, '', '#/');
  sessionStorage.clear();
  phone = false;
});

afterEach(() => { view?.unmount(); view = undefined; });

/** An overseer, its epic overseer, and the epic's workers, one of them in another root. */
async function showFleet() {
  hub = new FakeHub([
    session('overseer', 'fleet'),
    session('epic', 'fleet', 'Default/fleet/overseer'),
    session('worker-1', 'fleet', 'Default/fleet/epic'),
    session('worker-2', 'work', 'Default/fleet/epic'),
  ], [rootOf('fleet'), rootOf('work')]);
  await connectServers({ A: hub });
  view = await render(<Shell />);
  if (phone) await click(q<HTMLButtonElement>('.home-tab').find(b => b.textContent === 'Projects')!);
}

describe.each([false, true])('on a phone: %s', isPhone => {
  beforeEach(() => { phone = isPhone; });

  it('nests three levels, a child in another root marked with it', async () => {
    await showFleet();
    expect(outline()).toEqual(['overseer', '  epic', '    worker-1', '    worker-2 [work]']);
  });

  it('collapses a parent to a count of everything under it, and opens it again', async () => {
    await showFleet();
    await click(toggleOf('overseer')!);
    expect(outline()).toEqual(['overseer']);
    expect(toggleOf('overseer')!.querySelector('.project-children-count')?.textContent).toBe('3');
    expect(toggleOf('overseer')!.getAttribute('aria-expanded')).toBe('false');

    await click(toggleOf('overseer')!);
    expect(outline()).toEqual(['overseer', '  epic', '    worker-1', '    worker-2 [work]']);
  });

  it('rolls a grandchild that needs the user up to its collapsed grandparent', async () => {
    await showFleet();
    hub.callbacks.onAttentionChanged?.([needs('Default/work/worker-2')]);
    await click(toggleOf('overseer')!);
    expect(toggleOf('overseer')!.querySelector('.project-children-attention')).not.toBeNull();
  });
});

it('shows no dot on a collapsed parent when nothing under it needs the user, nor on an open one', async () => {
  await showFleet();
  hub.callbacks.onAttentionChanged?.([needs('Default/fleet/worker-1')]);
  await vi.waitFor(() => expect(useAppStore.getState().attention).toHaveLength(1));
  expect(toggleOf('overseer')!.querySelector('.project-children-attention')).toBeNull();

  hub.callbacks.onAttentionChanged?.([]);
  await click(toggleOf('overseer')!);
  expect(toggleOf('overseer')!.querySelector('.project-children-attention')).toBeNull();
});

it('keeps a collapse on this device', async () => {
  await showFleet();
  await click(toggleOf('epic')!);
  expect(outline()).toEqual(['overseer', '  epic']);
  expect(JSON.parse(localStorage.getItem('godmode-collapsed-sessions')!)).toEqual({ [`A:Default/fleet/epic`]: true });
});

it("puts a child of another profile's session at the top of its own profile, noting its parent", async () => {
  hub = new FakeHub([
    session('overseer', 'fleet'),
    session('helper', 'side', 'Default/fleet/overseer', 'Mega'),
  ], [rootOf('fleet'), rootOf('side', 'Mega')]);
  await connectServers({ A: hub });
  view = await render(<Shell />);

  const mega = q('.profile-group').find(g => g.querySelector('.profile-group-name')?.textContent === 'Mega')!;
  expect(q('.project-name', mega).map(e => e.textContent)).toEqual(['helper']);
  expect(mega.querySelector('.project-started-by')?.textContent).toBe(' · started by overseer');
  expect(toggleOf('overseer')).toBeNull();
});
