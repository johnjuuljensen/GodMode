// @vitest-environment jsdom
/**
 * Adopting folders (#370): under each root, a folded "Not in GodMode (N)" row lists the folders the server
 * says no session works in, each with Adopt, which makes a session of it and opens it. An adopted session's
 * delete offers "Forget (keep the folder)" beside Delete: it runs no delete script, and Undo brings it back.
 * Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRootInfo, ProjectSummary, UnmanagedFolder } from '../../signalr/types';
import { FakeHub, connectServers, project } from '../../test/fakeHub';
import { render, click, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { dismissToast } from '../../toast';
import { dismissConfirm } from '../../confirmDialog';
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

const work: ProjectRootInfo = {
  Name: 'work', ProfileName: 'Default',
  Actions: [{ Name: 'issue', AllowSkipPermissions: false, Session: true, Transient: false }],
};

const folders = (): UnmanagedFolder[] => [
  { Path: 'feature-12', Name: 'Fix the list', Kind: 'feat', ActionName: 'issue', Inputs: { issue: 12, branch: 'feature/12-fix' } },
  { Path: 'old-share', Name: 'old-share', Inputs: { branch: 'main' } },
];

const session = (name: string, opts: { adopted?: boolean; shared?: boolean } = {}): ProjectSummary => ({
  ...project(`Default/work/260930-issue-${name}-abcd`, name, 'Idle', new Date().toISOString()),
  ActionName: 'issue', Kind: 'feat', SharedFolder: opts.shared ?? false, Adopted: opts.adopted ?? false,
});

const initialState = useAppStore.getState();
let view: Rendered | undefined;
let hub: FakeHub;

const q = <T extends Element = HTMLElement>(sel: string) => [...document.querySelectorAll<T>(sel)];
const toggle = () => document.querySelector<HTMLButtonElement>('.unmanaged-toggle');
const candidates = () => q('.unmanaged-item .project-name').map(e => e.textContent);
const row = (name: string) => q('.project-item').find(r => r.querySelector('.project-name')?.textContent === name);
const dialog = () => document.querySelector('.confirm-dialog');
const toast = () => document.querySelector('.toast');
const buttonIn = (el: Element | null | undefined, label: string) =>
  [...(el?.querySelectorAll<HTMLButtonElement>('button') ?? [])].find(b => b.textContent === label);

async function show(projects: ProjectSummary[], unmanaged: UnmanagedFolder[] = folders()) {
  hub = new FakeHub(projects, [work]);
  hub.unmanaged['Default/work'] = unmanaged;
  await connectServers({ A: hub });
  view = await render(<Shell />);
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  sessionStorage.clear();
});

afterEach(() => {
  act(() => { dismissToast(); dismissConfirm(); });
  view?.unmount();
  view = undefined;
});

describe('the folded group', () => {
  it('says how many folders the root has that are not in GodMode, and lists none until opened', async () => {
    await show([session('notes')]);

    await vi.waitFor(() => expect(toggle()?.textContent).toBe('Not in GodMode (2)'));
    expect(toggle()!.getAttribute('aria-expanded')).toBe('false');
    expect(candidates()).toEqual([]);
    expect(hub.listings).toContain('Default/work');
    // It is the root's, under its profile, below its sessions
    expect(toggle()!.closest('.root-group')?.querySelector('.root-group-name')?.textContent).toBe('work');
  });

  it('opens to list each folder, with its kind or its branch, and an Adopt each', async () => {
    await show([]);
    await vi.waitFor(() => expect(toggle()).not.toBeNull());

    await click(toggle()!);

    await vi.waitFor(() => expect(candidates()).toEqual(['Fix the list', 'old-share']));
    const [feature, share] = q('.unmanaged-item');
    expect(feature.querySelector('.kind-label')?.textContent).toBe('feat');
    expect(share.querySelector('.project-meta')?.textContent).toBe('main');
    expect(q('.unmanaged-adopt-btn').map(b => b.textContent)).toEqual(['Adopt', 'Adopt']);
    expect(toggle()!.getAttribute('aria-expanded')).toBe('true');
    expect(hub.adopted).toEqual([]);
  });

  it('is not there when the root has no such folder', async () => {
    await show([session('notes')], []);
    await vi.waitFor(() => expect(hub.listings).toContain('Default/work'));
    expect(toggle()).toBeNull();
  });

  it("says why when the root's list fails, once opened", async () => {
    hub = new FakeHub([], [work]);
    hub.failList = 'list.ps1 printed what is not the list';
    await connectServers({ A: hub });
    view = await render(<Shell />);

    await vi.waitFor(() => expect(toggle()?.textContent).toBe('Not in GodMode (?)'));
    await click(toggle()!);
    await vi.waitFor(() => expect(document.querySelector('.unmanaged-error')?.textContent).toContain('printed what is not the list'));
  });
});

describe('Adopt', () => {
  it("adopts the folder with its listing's action and inputs, opens the session, and the folder leaves the group", async () => {
    await show([]);
    await vi.waitFor(() => expect(toggle()).not.toBeNull());
    await click(toggle()!);
    await vi.waitFor(() => expect(candidates()).toHaveLength(2));

    await click(q<HTMLButtonElement>('.unmanaged-adopt-btn')[0]);

    await vi.waitFor(() => expect(hub.adopted).toHaveLength(1));
    expect(hub.adopted[0]).toEqual({
      profileName: 'Default', rootName: 'work', path: 'feature-12', actionName: 'issue',
      inputs: { name: 'Fix the list', kind: 'feat', issue: 12, branch: 'feature/12-fix' },
    });
    await vi.waitFor(() => expect(useAppStore.getState().selectedProject).toEqual({ serverId: 'A', projectId: 'Default/work/adopted-feature-12' }));
    // Listed as any other session, and read again: the folder is a session's now
    await vi.waitFor(() => expect(row('feature-12')).toBeDefined());
    await vi.waitFor(() => expect(toggle()?.textContent).toBe('Not in GodMode (1)'));
  });

  it('sends no action for a folder its listing gave none, and its name for a session no script names', async () => {
    await show([]);
    await vi.waitFor(() => expect(toggle()).not.toBeNull());
    await click(toggle()!);
    await vi.waitFor(() => expect(candidates()).toHaveLength(2));

    await click(q<HTMLButtonElement>('.unmanaged-adopt-btn')[1]);

    await vi.waitFor(() => expect(hub.adopted).toHaveLength(1));
    expect(hub.adopted[0]).toMatchObject({ path: 'old-share', actionName: null, inputs: { name: 'old-share', branch: 'main' } });
  });
});

describe("an adopted session's delete", () => {
  const openDelete = async (name: string) => {
    await click(row(name)!.querySelector<HTMLButtonElement>('.project-item-menu-btn')!);
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete…')!);
    await vi.waitFor(() => expect(dialog()).not.toBeNull());
  };

  it('offers Forget (keep the folder) beside Delete', async () => {
    await show([session('feature-12', { adopted: true })]);

    await openDelete('feature-12');

    expect([...dialog()!.querySelectorAll('button')].map(b => b.textContent)).toEqual(['Cancel', 'Forget (keep the folder)', 'Delete']);
    expect(dialog()!.querySelector('.confirm-dialog-message')?.textContent).toContain('keeps the folder');
  });

  it('forgets, running no delete, and Undo brings it back', async () => {
    await show([session('feature-12', { adopted: true })]);
    await openDelete('feature-12');

    await click(buttonIn(dialog(), 'Forget (keep the folder)')!);

    await vi.waitFor(() => expect(hub.forgotten).toEqual(['Default/work/260930-issue-feature-12-abcd']));
    expect(hub.deletes).toEqual([]);
    await vi.waitFor(() => expect(toast()?.textContent).toContain('Forgot "feature-12"; its folder stays'));
    await click(buttonIn(toast(), 'Undo')!);
    await vi.waitFor(() => expect(hub.restores).toEqual(['Default/work/260930-issue-feature-12-abcd']));
  });

  it('deletes as its root says when Delete is chosen', async () => {
    await show([session('feature-12', { adopted: true })]);
    await openDelete('feature-12');

    await click(buttonIn(dialog(), 'Delete')!);

    await vi.waitFor(() => expect(hub.deletes).toEqual([{ projectId: 'Default/work/260930-issue-feature-12-abcd', force: false }]));
    expect(hub.forgotten).toEqual([]);
  });

  it('asks, with Forget, even when it shares its folder; a session it did not adopt is offered no Forget', async () => {
    await show([session('shared', { adopted: true, shared: true }), session('made')]);

    await openDelete('shared');
    expect(buttonIn(dialog(), 'Forget (keep the folder)')).toBeDefined();
    await click(buttonIn(dialog(), 'Cancel')!);

    await openDelete('made');
    expect(buttonIn(dialog(), 'Forget (keep the folder)')).toBeUndefined();
  });
});
