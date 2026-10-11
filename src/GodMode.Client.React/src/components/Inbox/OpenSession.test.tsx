// @vitest-environment jsdom
/**
 * The open session's item (#549): the session this window's main view shows is on screen already, so the inbox leaves
 * its item out, and its count, the page's title and the left list's needs-you dots leave it out too. Hidden here only:
 * the store keeps it, and it is back once the view shows something else. The app is told which session is open
 * (session.open), to hold back its notification. Renders the Shell on the real store, in the list layout.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, AttentionKind } from '../../signalr/types';
import { FakeHub, project, root, connectServers, flush } from '../../test/fakeHub';
import { render, click, type Rendered } from '../../test/render';
import { answer, sent } from '../../test/appShell';
import { useAppStore, openSessionKey, projectKey } from '../../store';
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

const item = (projectId: string, kind: AttentionKind, since: string): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: since, Text: `${projectId} ${kind}`,
});
const items = [
  item('x', 'Finished', '2026-10-03T09:00:00Z'),
  item('y', 'Question', '2026-10-03T09:01:00Z'),
  item('z', 'Permission', '2026-10-03T09:02:00Z'),
];

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;

const listed = () => [...view.container.querySelectorAll('.inbox-item .inbox-item-name')].map(e => e.textContent);
const count = () => view.container.querySelector('.inbox-count')?.textContent;
const shownProject = () => view.container.querySelector('.project-view .project-header-name')?.textContent ?? null;
const open = (projectId: string) => act(async () => useAppStore.getState().selectProject('A', projectId));
const rootHeader = () => view.container.querySelector<HTMLElement>('.root-group-header')!;
const rootDot = () => rootHeader().querySelector('.project-children-attention');

async function start(attention: AttentionItem[] = items) {
  hub = new FakeHub([...['x', 'y', 'z', 'quiet'].map(id => project(id, id, 'Idle', '2026-10-03T09:00:00Z'))], [root]);
  await connectServers({ A: hub });
  await act(async () => { hub.callbacks.onAttentionChanged?.(attention); });
  view = await render(<Shell />);
  await flush();
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  answer('session.open', true);
});

afterEach(() => view.unmount());

describe('with a session open', () => {
  it('leaves its item out of the inbox and its count, and lists the others', async () => {
    await start();
    expect(listed()).toEqual(['x', 'y', 'z']);
    expect(count()).toBe('3');

    await open('x');
    expect(shownProject()).toBe('x');
    expect(listed()).toEqual(['y', 'z']);
    expect(count()).toBe('2');
    expect(document.title).toBe('(2) GodMode');
    // Hidden, not gone: the store and the server still have it
    expect(useAppStore.getState().attention.map(i => i.ProjectId)).toEqual(['x', 'y', 'z']);
  });

  it('brings its item back when the view leaves it: another session, none, or a page over it', async () => {
    await start();
    await open('x');
    await open('y');
    expect(listed()).toEqual(['x', 'z']);

    await act(async () => useAppStore.getState().clearSelection());
    expect(listed()).toEqual(['x', 'y', 'z']);
    expect(count()).toBe('3');
    expect(document.title).toBe('(3) GodMode');

    await open('x');
    await act(async () => useAppStore.getState().setActivePage({ type: 'addServer' }));
    expect(useAppStore.getState().selectedProject).toEqual({ serverId: 'A', projectId: 'x' });
    expect(listed()).toEqual(['x', 'y', 'z']);
  });

  it("puts no needs-you dot on a folded root for it, and does once it is left", async () => {
    await start([items[0]]);
    await open('x');
    await click(rootHeader().querySelector<HTMLButtonElement>('.group-fold-toggle')!);
    expect(rootDot()).toBeNull();

    // Opening another session unfolds its root: fold it again
    await open('quiet');
    await click(rootHeader().querySelector<HTMLButtonElement>('.group-fold-toggle')!);
    expect(rootDot()).not.toBeNull();
  });

  it('tells the app which session is open, and when none is', async () => {
    await start();
    await open('x');
    await act(async () => useAppStore.getState().clearSelection());
    await flush();
    expect(sent.filter(m => m.Type === 'session.open').map(m => m.Payload)).toEqual([
      { Session: null },
      { Session: { ServerId: 'A', ProjectId: 'x' } },
      { Session: null },
    ]);
  });
});

it('names the open session by its key, and none while a page covers it', () => {
  const selectedProject = { serverId: 'A', projectId: 'x' };
  expect(openSessionKey({ selectedProject, activePage: null })).toBe(projectKey('A', 'x'));
  expect(openSessionKey({ selectedProject, activePage: { type: 'appSettings' } })).toBeNull();
  expect(openSessionKey({ selectedProject: null, activePage: null })).toBeNull();
});
