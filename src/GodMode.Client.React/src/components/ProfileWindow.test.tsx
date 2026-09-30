// @vitest-environment jsdom
/**
 * Profile windows (#340): a page the app opens in a profile's own window is locked to that profile, by name across
 * every server. Its list, tiles, inbox and title count are that profile's only, its filter is hidden, and it offers
 * no window of its own. The main window is unlocked, and where the app has windows (Windows) each profile's header
 * opens that profile in its own. Renders the Shell on the real store.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, ProjectRootInfo } from '../signalr/types';
import { FakeHub, connectServers, flush, project } from '../test/fakeHub';
import { render, click, type Rendered } from '../test/render';
import { useAppStore } from '../store';
import { Shell } from './Shell';

/** What the app answers window.info with, and the profiles it was asked to open. */
const host = vi.hoisted(() => ({
  window: { Profile: null as string | null, CanOpenWindows: true },
  opened: [] as string[],
}));

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  windowInfo: async () => host.window,
  openProfileWindow: async (profile: string) => { host.opened.push(profile); },
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const rootOf = (name: string, profile: string): ProjectRootInfo => ({
  Name: name, ProfileName: profile,
  Actions: [{ Name: 'issue', AllowSkipPermissions: false, Session: true, Transient: false, InputSchema: { type: 'object', properties: { title: { type: 'string', title: 'Title' } } } }],
});
const inProfile = (id: string, profile: string, rootName: string) =>
  ({ ...project(id, id, 'WaitingInput', '2026-09-29T12:00:00Z'), ProfileName: profile, RootName: rootName });
const needs = (projectId: string, profile: string): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Profile: profile, Kind: 'Question', Since: '2026-09-29T12:00:00Z', Text: `${projectId} asks`,
});

const initialState = useAppStore.getState();
let view: Rendered | undefined;
let hubA: FakeHub;
let hubB: FakeHub;

const q = <T extends Element = HTMLElement>(sel: string) => [...view!.container.querySelectorAll<T>(sel)];
const texts = (sel: string) => q(sel).map(e => e.textContent);

/**
 * Two servers, and the Work profile on both, as a name: A has Work's ship and chat and Default's home, B Work's ship
 * and Default's home. Each project asks a question.
 */
async function open(window: { Profile: string | null; CanOpenWindows: boolean }) {
  host.window = window;
  hubA = new FakeHub(
    [inProfile('a-ship', 'Work', 'ship'), inProfile('a-chat', 'Work', 'chat'), inProfile('a-home', 'Default', 'home')],
    [rootOf('ship', 'Work'), rootOf('chat', 'Work'), rootOf('home', 'Default')]);
  hubB = new FakeHub(
    [inProfile('b-ship', 'Work', 'ship'), inProfile('b-home', 'Default', 'home')],
    [rootOf('ship', 'Work'), rootOf('home', 'Default')]);
  await useAppStore.getState().loadWindow();
  await connectServers({ A: hubA, B: hubB });
  hubA.callbacks.onAttentionChanged?.([needs('a-ship', 'Work'), needs('a-home', 'Default')]);
  hubB.callbacks.onAttentionChanged?.([needs('b-ship', 'Work'), needs('b-home', 'Default')]);
  view = await render(<Shell />);
  await flush();
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  sessionStorage.clear();
  localStorage.clear();
  document.title = 'GodMode';
  host.opened = [];
});

afterEach(() => { view?.unmount(); view = undefined; });

describe('a page locked to a profile', () => {
  beforeEach(() => open({ Profile: 'Work', CanOpenWindows: true }));

  it("lists only that profile's roots and projects, from every server", () => {
    expect(texts('.profile-group-name')).toEqual(['Work']);
    expect(texts('.root-group-name')).toEqual(['chat', 'ship (Server A)', 'ship (Server B)']);
    expect(texts('.project-name').sort()).toEqual(['a-chat', 'a-ship', 'b-ship']);
  });

  it("shows only that profile's tiles", async () => {
    await click(q<HTMLButtonElement>('.sidebar-add-btn[title="Tile view"]')[0]);
    expect(texts('.tile-name').sort()).toEqual(['a-chat', 'a-ship', 'b-ship']);
  });

  it("keeps only that profile's attention items, in its inbox and in its title count", () => {
    expect(useAppStore.getState().attention.map(i => i.ProjectId).sort()).toEqual(['a-ship', 'b-ship']);
    expect(q('.inbox-item').map(e => e.textContent?.includes('b-ship') || e.textContent?.includes('a-ship'))).toEqual([true, true]);
    expect(document.title).toBe('Work (2) - GodMode');
  });

  it('hides the profile filter, and a filter set anyway changes nothing', () => {
    expect(q('.sidebar-profile-filter')).toHaveLength(0);
    useAppStore.getState().setProfileFilter('All');
    expect(useAppStore.getState().profileFilter).toBe('Work');
  });

  it('offers no window of its own', () => {
    expect(q('.profile-window-btn')).toHaveLength(0);
  });

  it("offers only that profile's roots to create in, on the servers that have one", async () => {
    await click(q<HTMLButtonElement>('.sidebar-add-btn[title="Create project"]')[0]);
    expect(texts('.root-picker-card-name')).toEqual(['ship', 'chat']);
    expect(q('.root-picker-profile')).toHaveLength(0);
  });
});

describe('the main window', () => {
  it('lists every profile, with the filter, and its title counts every item', async () => {
    await open({ Profile: null, CanOpenWindows: true });
    expect(texts('.profile-group-name')).toEqual(['Default', 'Work']);
    expect(q('.sidebar-profile-filter')).toHaveLength(1);
    expect(document.title).toBe('(4) GodMode');
  });

  it("opens a profile in its own window from the profile's header", async () => {
    await open({ Profile: null, CanOpenWindows: true });
    const work = q('.profile-group').find(g => g.querySelector('.profile-group-name')?.textContent === 'Work')!;
    await click(work.querySelector<HTMLButtonElement>('.profile-window-btn')!);
    expect(host.opened).toEqual(['Work']);
  });

  it('offers no window where the app has none (Android)', async () => {
    await open({ Profile: null, CanOpenWindows: false });
    expect(q('.profile-window-btn')).toHaveLength(0);
  });
});
