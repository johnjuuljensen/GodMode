// @vitest-environment jsdom
/**
 * The create page creates on the server and root it shows, with what was typed (#142, #240): its server
 * is pinned, the route names its root, and each root's form restores its own draft. Renders the Shell,
 * which mounts one form per route and keeps the URL in step, on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRootInfo } from '../../signalr/types';
import { FakeHub, connectServers, status } from '../../test/fakeHub';
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
}));

const rootNamed = (name: string, profile = 'Default'): ProjectRootInfo => ({
  Name: name, ProfileName: profile,
  Actions: [{ Name: 'issue', AllowSkipPermissions: false, Session: true, Transient: false, InputSchema: { type: 'object', properties: { title: { type: 'string', title: 'Title' } }, required: ['title'] } }],
});

/** A root whose schema has the Skip Permissions toggle, defaulting on as the dev root's once did; allowed or not by the root. */
const rootWithSkip = (allowSkipPermissions: boolean): ProjectRootInfo => ({
  Name: 'work', ProfileName: 'Default',
  Actions: [{
    Name: 'issue', AllowSkipPermissions: allowSkipPermissions, Session: true, Transient: false,
    InputSchema: {
      type: 'object',
      properties: {
        title: { type: 'string', title: 'Title' },
        skipPermissions: { type: 'boolean', title: 'Skip Permissions', default: 'true' },
      },
      required: ['title'],
    },
  }],
});

const initialState = useAppStore.getState();
let view: Rendered | undefined;

const button = (label: string) => [...view!.container.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === label)!;
const titleField = () => [...view!.container.querySelectorAll('.form-group')]
  .find(g => g.querySelector('label')?.textContent?.startsWith('Title'))!.querySelector('input')!;
const skipToggle = () => [...view!.container.querySelectorAll('.form-group')]
  .find(g => g.querySelector('label')?.textContent === 'Skip Permissions')?.querySelector<HTMLInputElement>('input[type="checkbox"]') ?? null;
const shownRoot = () => view!.container.querySelector('.selected-root-name')?.textContent;
const shownServer = () => view!.container.querySelector('.selected-root-server')?.textContent;
const rootCard = (name: string) => [...view!.container.querySelectorAll<HTMLElement>('.root-picker-card')]
  .find(el => el.querySelector('.root-picker-card-name')?.textContent === name)!;
const openPage = (page: ActivePage | null) => act(async () => useAppStore.getState().setActivePage(page));
/** A root's "+": the create page for that root, in its profile. */
const openFor = (serverId: string, rootName: string, profileName = 'Default') =>
  openPage({ type: 'createProject', context: { serverId, profileName, rootName } });

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
    expect(hubB.created).toEqual([{ profileName: 'Default', rootName: 'work', actionName: 'issue', inputs: { model: 'opus', title: 'Fix the login' } }]);
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
    expect(location.hash).toBe('#/create/A/Default/play');

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

describe('two profiles with a root of one name', () => {
  let hub: FakeHub;

  beforeEach(async () => {
    hub = new FakeHub([], [rootNamed('work', 'Private'), rootNamed('work', 'Mega')]);
    await connectServers({ A: hub });
    view = await render(<Shell />);
  });

  it("creates in the + root's own profile", async () => {
    await openFor('A', 'work', 'Mega');
    expect(location.hash).toBe('#/create/A/Mega/work');
    await typeInto(titleField(), 'Fix the login');
    await click(button('Create'));

    expect(hub.created).toEqual([{ profileName: 'Mega', rootName: 'work', actionName: 'issue', inputs: { model: 'opus', title: 'Fix the login' } }]);
  });

  it("keeps a draft for each profile's root", async () => {
    await openFor('A', 'work', 'Mega');
    await typeInto(titleField(), 'Mega draft');

    await openFor('A', 'work', 'Private');
    expect(titleField().value).toBe('');
    await typeInto(titleField(), 'Private draft');

    await openFor('A', 'work', 'Mega');
    expect(titleField().value).toBe('Mega draft');
  });
});

describe('the Skip Permissions toggle (#233)', () => {
  async function openFormOf(root: ProjectRootInfo) {
    const hub = new FakeHub([], [root]);
    await connectServers({ A: hub });
    view = await render(<Shell />);
    await openFor('A', root.Name);
    await typeInto(titleField(), 'Fix the login');
    return hub;
  }

  it('is not shown, and not sent, where the root does not allow it', async () => {
    const hub = await openFormOf(rootWithSkip(false));

    expect(skipToggle()).toBeNull();
    await click(button('Create'));
    expect(hub.created).toEqual([{ profileName: 'Default', rootName: 'work', actionName: 'issue', inputs: { model: 'opus', title: 'Fix the login' } }]);
  });

  it('is shown unchecked where the root allows it, whatever the schema defaults it to', async () => {
    const hub = await openFormOf(rootWithSkip(true));

    expect(skipToggle()?.checked).toBe(false);
    await click(button('Create'));
    expect(hub.created.map(c => c.inputs.skipPermissions)).toEqual([false]);
  });

  it('is sent checked where the root allows it and the user checks it', async () => {
    const hub = await openFormOf(rootWithSkip(true));

    await click(skipToggle()!);
    expect(skipToggle()?.checked).toBe(true);
    await click(button('Create'));
    expect(hub.created.map(c => c.inputs.skipPermissions)).toEqual([true]);
  });
});

describe('an action that starts no session (#324)', () => {
  /** A provisioning root: its action only runs a script, which makes a new root. */
  const provisioning: ProjectRootInfo = {
    Name: 'experiments', ProfileName: 'Default',
    Actions: [{ Name: 'new-root', AllowSkipPermissions: false, Session: false, Transient: false, InputSchema: { type: 'object', properties: { title: { type: 'string', title: 'Title' } }, required: ['title'] } }],
  };
  const finishedView = () => view!.container.querySelector('.form-success')?.textContent ?? null;
  const modelPicker = () => [...view!.container.querySelectorAll('.form-group')].find(g => g.querySelector('label')?.textContent === 'Model') ?? null;

  async function openProvisioning(message: string | null) {
    const hub = new FakeHub([], [provisioning]);
    hub.createResult = { Project: null, Message: message };
    await connectServers({ A: hub });
    view = await render(<Shell />);
    await openFor('A', 'experiments');
    await typeInto(titleField(), 'Try it');
    return hub;
  }

  it('offers no model, and runs with what was typed', async () => {
    const hub = await openProvisioning('Root try-it is ready');

    expect(modelPicker()).toBeNull();
    await click(button('Run'));
    expect(hub.created).toEqual([{ profileName: 'Default', rootName: 'experiments', actionName: 'new-root', inputs: { title: 'Try it' } }]);
  });

  it('shows the message and stays where it was, with the form back to its defaults', async () => {
    await openProvisioning('Root try-it is ready');

    await click(button('Run'));

    expect(finishedView()).toBe('Root try-it is ready');
    expect(useAppStore.getState().selectedProject).toBeNull();
    expect(useAppStore.getState().activePage).toEqual({ type: 'createProject', context: { serverId: 'A', profileName: 'Default', rootName: 'experiments' } });
    expect(shownRoot()).toBe('experiments');
    expect(titleField().value).toBe('');
  });

  it('says the action finished when its script gave no message', async () => {
    await openProvisioning(null);

    await click(button('Run'));

    expect(finishedView()).toBe('new-root finished.');
    expect(useAppStore.getState().selectedProject).toBeNull();
  });
});

describe('the default form (#352)', () => {
  /** An action with no schema.json of its own: the server's default schema, which requires only the name. */
  const withSchema = (required: string[]): ProjectRootInfo => ({
    Name: 'assistant', ProfileName: 'Default',
    Actions: [{
      Name: 'chat', AllowSkipPermissions: false, Session: true, Transient: false,
      InputSchema: {
        type: 'object',
        properties: {
          name: { type: 'string', title: 'Project Name' },
          prompt: { type: 'string', title: 'Task Description', 'x-multiline': true },
        },
        required,
      },
    }],
  });
  const field = (title: string) => [...view!.container.querySelectorAll('.form-group')]
    .find(g => g.querySelector('label')?.textContent?.startsWith(title))!;
  const formError = () => view!.container.querySelector('.form-error')?.textContent ?? null;

  async function openWith(root: ProjectRootInfo) {
    const hub = new FakeHub([], [root]);
    hub.createResult = { Project: status('new1', 'Idle'), Message: null };
    await connectServers({ A: hub });
    view = await render(<Shell />);
    await openFor('A', 'assistant');
    await typeInto(field('Project Name').querySelector('input')!, 'Outbound');
    return hub;
  }

  it('marks only the name required, and creates with the description left empty, opening the session', async () => {
    const hub = await openWith(withSchema(['name']));

    expect(field('Project Name').querySelector('.form-required')).not.toBeNull();
    expect(field('Task Description').querySelector('.form-required')).toBeNull();
    await click(button('Create'));

    expect(formError()).toBeNull();
    expect(hub.created).toEqual([{ profileName: 'Default', rootName: 'assistant', actionName: 'chat', inputs: { model: 'opus', name: 'Outbound' } }]);
    expect(useAppStore.getState().selectedProject).toEqual({ serverId: 'A', projectId: 'new1' });
  });

  it("still refuses an empty description where the root's own schema requires it", async () => {
    const hub = await openWith(withSchema(['name', 'prompt']));

    await click(button('Create'));

    expect(formError()).toBe('"Task Description" is required');
    expect(hub.created).toEqual([]);
  });
});

it('leaving the page discards the drafts', async () => {
  const hub = new FakeHub([], [rootNamed('work')]);
  await connectServers({ A: hub });
  const context = { serverId: 'A', profileName: 'Default', rootName: 'work' };
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

describe('model and effort (#226)', () => {
  const rootWith = (model: string | null, effort: string | null): ProjectRootInfo => ({
    Name: 'work', ProfileName: 'Default',
    Actions: [{ ...rootNamed('work').Actions![0], Model: model, Effort: effort }],
  });
  const pickerOf = (label: string) => [...view!.container.querySelectorAll('.form-group')]
    .find(g => g.querySelector('label')?.textContent === label)?.querySelector('select') ?? null;
  const choose = (select: HTMLSelectElement, value: string) => act(async () => {
    select.value = value;
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });

  async function openFormOf(root: ProjectRootInfo) {
    const hub = new FakeHub([], [root]);
    await connectServers({ A: hub });
    view = await render(<Shell />);
    await openFor('A', root.Name);
    await typeInto(titleField(), 'Fix the login');
    return hub;
  }

  it('offers the current presets', async () => {
    await openFormOf(rootWith(null, null));

    expect([...pickerOf('Model')!.options].map(o => o.value)).toEqual(['fable', 'opus', 'sonnet', 'haiku', '__other__']);
    expect([...pickerOf('Effort')!.options].map(o => o.value)).toEqual(['', 'low', 'medium', 'high', 'xhigh', 'max']);
  });

  it('shows a configured model that is not a preset as selected, and sends it', async () => {
    const hub = await openFormOf(rootWith('opus[1m]', null));

    const model = pickerOf('Model')!;
    expect(model.value).toBe('opus[1m]');
    expect(model.selectedOptions[0].textContent).toBe('opus[1m]');
    await click(button('Create'));
    expect(hub.created.map(c => c.inputs.model)).toEqual(['opus[1m]']);
  });

  it('takes a model name typed in as Other', async () => {
    const hub = await openFormOf(rootWith('opus', null));

    await choose(pickerOf('Model')!, '__other__');
    const typed = view!.container.querySelector<HTMLInputElement>('input[aria-label="Model name"]')!;
    await typeInto(typed, 'claude-fable-5');
    await act(async () => typed.blur());
    expect(pickerOf('Model')!.value).toBe('claude-fable-5');
    await click(button('Create'));
    expect(hub.created.map(c => c.inputs.model)).toEqual(['claude-fable-5']);
  });

  it("starts from the action's effort, and sends it", async () => {
    const hub = await openFormOf(rootWith('fable', 'xhigh'));

    expect(pickerOf('Effort')!.value).toBe('xhigh');
    await click(button('Create'));
    expect(hub.created.map(c => c.inputs)).toEqual([{ model: 'fable', effort: 'xhigh', title: 'Fix the login' }]);
  });

  it("sends the effort chosen over the action's, and the empty default as empty", async () => {
    const hub = await openFormOf(rootWith('fable', 'xhigh'));

    await choose(pickerOf('Effort')!, 'low');
    await click(button('Create'));
    await openFor('A', 'work');
    await typeInto(titleField(), 'Fix the login');
    await choose(pickerOf('Effort')!, '');
    await click(button('Create'));

    expect(hub.created.map(c => c.inputs.effort)).toEqual(['low', '']);
  });
});
