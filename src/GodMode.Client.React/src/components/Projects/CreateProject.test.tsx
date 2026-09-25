// @vitest-environment jsdom
/**
 * The create page creates on the server and root it shows, with what was typed (#142, #240): its server
 * is pinned, the route names its root, and each root's form restores its own draft. Renders the Shell,
 * which mounts one form per route and keeps the URL in step, on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRootInfo } from '../../signalr/types';
import { FakeHub, connectServers } from '../../test/fakeHub';
import { render, typeInto, click, type Rendered } from '../../test/render';
import { useAppStore, type ActivePage } from '../../store';
import { Shell } from '../Shell';
import { CreateProject } from './CreateProject';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
  isMaui: false,
  clearApiKey: () => {},
}));

const rootNamed = (name: string): ProjectRootInfo => ({
  Name: name, ProfileName: 'Default',
  Actions: [{ Name: 'issue', InputSchema: { type: 'object', properties: { title: { type: 'string', title: 'Title' } }, required: ['title'] } }],
});

const initialState = useAppStore.getState();
let view: Rendered | undefined;

const button = (label: string) => [...view!.container.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === label)!;
const titleField = () => [...view!.container.querySelectorAll('.form-group')]
  .find(g => g.querySelector('label')?.textContent?.startsWith('Title'))!.querySelector('input')!;
const shownRoot = () => view!.container.querySelector('.selected-root-name')?.textContent;
const shownServer = () => view!.container.querySelector('.selected-root-server')?.textContent;
const rootCard = (name: string) => [...view!.container.querySelectorAll<HTMLElement>('.root-picker-card')]
  .find(el => el.querySelector('.root-picker-card-name')?.textContent === name)!;
const openPage = (page: ActivePage | null) => act(async () => useAppStore.getState().setActivePage(page));
/** A root's "+": the create page for that root. */
const openFor = (serverId: string, rootName: string) => openPage({ type: 'createProject', context: { serverId, rootName } });

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  sessionStorage.clear();
});

afterEach(() => { view?.unmount(); view = undefined; });

describe('with a second server connecting late', () => {
  it('keeps its server and values when the second server finishes connecting', async () => {
    const hubA = new FakeHub([], [rootNamed('work')]);
    const hubB = new FakeHub([], [rootNamed('work')]);
    // Server A comes first in the list, but is not reachable yet: a phone waking up
    hubA.failConnect = true;
    await connectServers({ A: hubA, B: hubB });
    view = await render(<Shell />);

    await click(view.container.querySelector<HTMLElement>('.sidebar-add-btn[title="Create project"]')!);
    // One root: straight to its form, on the one server listing roots
    expect(shownRoot()).toBe('work');
    expect(shownServer()).toBe('on Server B');
    await typeInto(titleField(), 'Fix the login');

    hubA.failConnect = false;
    await act(async () => useAppStore.getState().connectServer('A'));

    expect(shownServer()).toBe('on Server B');
    expect(titleField().value).toBe('Fix the login');
    await click(button('Create'));
    expect(hubB.created).toEqual([{ rootName: 'work', actionName: 'issue', inputs: { model: 'opus', title: 'Fix the login' } }]);
    expect(hubA.created).toEqual([]);
  });
});

describe('on one server with two roots', () => {
  let hub: FakeHub;

  beforeEach(async () => {
    hub = new FakeHub([], [rootNamed('work'), rootNamed('play')]);
    await connectServers({ A: hub });
    view = await render(<Shell />);
  });

  /** A reload: the store starts over from the URL, and sessionStorage is what is left. */
  async function reload() {
    view!.unmount();
    useAppStore.setState(initialState, true);
    history.replaceState(null, '', location.hash);
    await connectServers({ A: hub });
    view = await render(<Shell />);
  }

  it('a reload after "Change" root restores the root picked, with its draft', async () => {
    await openFor('A', 'work');
    await click(button('Change'));
    await click(rootCard('play'));
    await typeInto(titleField(), 'Play draft');
    expect(location.hash).toBe('#/create/A/play');

    await reload();

    expect(shownRoot()).toBe('play');
    expect(titleField().value).toBe('Play draft');
  });

  it("another root's + while the page is open shows that root's form, and each keeps its own draft", async () => {
    await openFor('A', 'work');
    await typeInto(titleField(), 'Work draft');

    await openFor('A', 'play');
    expect(shownRoot()).toBe('play');
    expect(titleField().value).toBe('');
    await typeInto(titleField(), 'Play draft');

    await openFor('A', 'work');
    expect(shownRoot()).toBe('work');
    expect(titleField().value).toBe('Work draft');
  });
});

it('leaving the page discards the drafts', async () => {
  const hub = new FakeHub([], [rootNamed('work')]);
  await connectServers({ A: hub });
  const context = { serverId: 'A', rootName: 'work' };
  await openPage({ type: 'createProject', context });
  // Without the Shell, so leaving touches no history
  view = await render(<CreateProject context={context} />);
  await typeInto(titleField(), 'Work draft');
  await openPage(null);
  view.unmount();

  await openPage({ type: 'createProject', context });
  view = await render(<CreateProject context={context} />);
  expect(titleField().value).toBe('');
});
