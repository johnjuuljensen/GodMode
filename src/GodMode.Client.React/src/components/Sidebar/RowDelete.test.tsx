// @vitest-environment jsdom
/**
 * A session is deleted from its row without opening it (#325), and never by a bare button: the row's ⋯
 * menu or a right-click on a desktop, a swipe to the left on a phone. A session that shares its folder
 * goes at once, with "Deleted · Undo"; a worktree's delete asks first, naming the session and what goes.
 * Older sessions fold under "N older" in their root. Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ProjectRootInfo, ProjectState, ProjectSummary } from '../../signalr/types';
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

const DAY = 24 * 60 * 60 * 1000;
const ago = (ms: number) => new Date(Date.now() - ms).toISOString();

const work = (transient = false): ProjectRootInfo => ({
  Name: 'work', ProfileName: 'Default',
  Actions: [{ Name: 'chat', AllowSkipPermissions: false, Session: true, Transient: transient }],
});

const session = (name: string, opts: { state?: ProjectState; updated?: string; shared?: boolean; kind?: string } = {}): ProjectSummary => ({
  ...project(`Default/work/260929-chat-${name}-abcd`, name, opts.state ?? 'Stopped', opts.updated ?? ago(60_000)),
  ActionName: 'chat', SharedFolder: opts.shared ?? false, Kind: opts.kind ?? 'chat',
});

const initialState = useAppStore.getState();
let view: Rendered | undefined;
let hub: FakeHub;

const q = <T extends Element = HTMLElement>(sel: string) => [...document.querySelectorAll<T>(sel)];
const row = (name: string) => q('.project-item').find(r => r.querySelector('.project-name')?.textContent === name);
const names = () => q('.project-item .project-name').map(e => e.textContent);
const toast = () => document.querySelector('.toast');
const dialog = () => document.querySelector('.confirm-dialog');
const buttonIn = (el: Element | null | undefined, label: string) =>
  [...(el?.querySelectorAll<HTMLButtonElement>('button') ?? [])].find(b => b.textContent === label);

async function show(projects: ProjectSummary[], root = work()) {
  hub = new FakeHub(projects, [root]);
  await connectServers({ A: hub });
  view = await render(<Shell />);
}

/** Opens the row's ⋯ menu, as a click on it does. */
const openMenu = (name: string) => click(row(name)!.querySelector<HTMLButtonElement>('.project-item-menu-btn')!);

/** A touch drag along the row, from x0 to x1, then let go. */
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

describe('a row', () => {
  it('has no bare Delete button: only a ⋯ that opens a menu', async () => {
    await show([session('notes', { shared: true })]);

    expect(buttonIn(row('notes'), 'Delete')).toBeUndefined();
    expect(q('.project-item-swipe-delete')).toHaveLength(0);
    expect(document.querySelector('.project-item-menu')).toBeNull();

    await openMenu('notes');
    expect(q('.project-item-menu [role=menuitem]').map(b => b.textContent)).toEqual(['Delete']);
    expect(hub.deletes).toEqual([]);
  });

  it('opens the same menu on a right-click, and not the session', async () => {
    await show([session('notes', { shared: true })]);

    const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 30 });
    await act(async () => { row('notes')!.dispatchEvent(event); });

    expect(event.defaultPrevented).toBe(true);
    expect(q('.project-item-menu [role=menuitem]').map(b => b.textContent)).toEqual(['Delete']);
    expect(useAppStore.getState().selectedProject).toBeNull();
  });
  it('opens its menu outside the sidebar, which clips a fixed menu, and on the screen near its edge', async () => {
    await show([session('notes', { shared: true })]);

    const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: window.innerWidth - 4, clientY: window.innerHeight - 4 });
    await act(async () => { row('notes')!.dispatchEvent(event); });

    const menu = document.querySelector<HTMLElement>('.project-item-menu')!;
    expect(menu).not.toBeNull();
    expect(menu.closest('.shell-sidebar')).toBeNull();
    expect(menu.parentElement).toBe(document.body);
    expect(parseFloat(menu.style.left) + 160).toBeLessThanOrEqual(window.innerWidth);
    expect(parseFloat(menu.style.top) + 44).toBeLessThanOrEqual(window.innerHeight);

    await openMenu('notes');
    await openMenu('notes');
    expect(document.querySelector('.project-item-menu')?.closest('.shell-sidebar')).toBeNull();
  });
});

describe('a session-only delete', () => {
  it('goes at once from the ⋯ menu, with no dialog, and Undo restores it', async () => {
    await show([session('notes', { shared: true }), session('other', { shared: true })]);

    await openMenu('notes');
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);

    await vi.waitFor(() => expect(hub.deletes).toEqual([{ projectId: 'Default/work/260929-chat-notes-abcd', force: false }]));
    expect(dialog()).toBeNull();
    await vi.waitFor(() => expect(toast()?.textContent).toContain('Deleted "notes"'));
    const undo = buttonIn(toast(), 'Undo');
    expect(undo).toBeDefined();

    await click(undo!);

    await vi.waitFor(() => expect(hub.restores).toEqual(['Default/work/260929-chat-notes-abcd']));
    expect(toast()).toBeNull();
  });

  it('keeps the first Undo when a second delete follows at once, and each undoes its own', async () => {
    await show([session('a', { shared: true }), session('b', { shared: true }), session('c', { shared: true })]);

    await openMenu('a');
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);
    await vi.waitFor(() => expect(q('.toast')).toHaveLength(1));
    await openMenu('b');
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);
    await vi.waitFor(() => expect(q('.toast')).toHaveLength(2));

    expect(q('.toast').map(t => t.querySelector('.toast-text')?.textContent)).toEqual(['Deleted "a"', 'Deleted "b"']);
    const [first, second] = q('.toast');
    await click(buttonIn(first, 'Undo')!);
    await vi.waitFor(() => expect(hub.restores).toEqual(['Default/work/260929-chat-a-abcd']));
    expect(q('.toast').map(t => t.querySelector('.toast-text')?.textContent)).toEqual(['Deleted "b"']);

    await click(buttonIn(second, 'Undo')!);
    await vi.waitFor(() => expect(hub.restores).toEqual(['Default/work/260929-chat-a-abcd', 'Default/work/260929-chat-b-abcd']));
    expect(q('.toast')).toHaveLength(0);
  });

  it('ends each Undo after its own window, not the latest one', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      await show([session('a', { shared: true }), session('b', { shared: true })]);
      await openMenu('a');
      await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);
      await vi.waitFor(() => expect(q('.toast')).toHaveLength(1));
      await act(async () => { vi.advanceTimersByTime(5_000); });
      await openMenu('b');
      await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);
      await vi.waitFor(() => expect(q('.toast')).toHaveLength(2));

      await act(async () => { vi.advanceTimersByTime(5_500); });
      expect(q('.toast .toast-text').map(t => t.textContent)).toEqual(['Deleted "b"']);
      await act(async () => { vi.advanceTimersByTime(5_000); });
      expect(q('.toast')).toHaveLength(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it('offers Undo for a while, then no longer', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      await show([session('notes', { shared: true })]);
      await openMenu('notes');
      await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);
      await vi.waitFor(() => expect(buttonIn(toast(), 'Undo')).toBeDefined());

      await act(async () => { vi.advanceTimersByTime(9_000); });
      expect(buttonIn(toast(), 'Undo')).toBeDefined();
      await act(async () => { vi.advanceTimersByTime(1_500); });
      expect(toast()).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('says why when the delete is refused, and deletes nothing more', async () => {
    await show([session('notes', { shared: true })]);
    hub.failDelete = 'the delete script refused';

    await openMenu('notes');
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);

    await vi.waitFor(() => expect(toast()?.textContent).toContain('the delete script refused'));
    expect(buttonIn(toast(), 'Undo')).toBeUndefined();
  });

  it('forces a running claude, as the view has always done', async () => {
    await show([session('notes', { shared: true, state: 'Running' })]);
    await openMenu('notes');
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete')!);
    await vi.waitFor(() => expect(hub.deletes).toEqual([{ projectId: 'Default/work/260929-chat-notes-abcd', force: true }]));
  });
});

describe('a worktree delete', () => {
  it('asks first, naming the session and what goes, and deletes only once confirmed, with no Undo', async () => {
    await show([session('left-list', { kind: 'feat' })]);

    await openMenu('left-list');
    expect(q('.project-item-menu [role=menuitem]').map(b => b.textContent)).toEqual(['Delete…']);
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete…')!);

    await vi.waitFor(() => expect(dialog()).not.toBeNull());
    expect(dialog()!.querySelector('h2')?.textContent).toBe('Delete the feat session "left-list"?');
    const message = dialog()!.querySelector('.confirm-dialog-message')?.textContent ?? '';
    expect(message).toContain("the work root's delete script");
    expect(message).toContain('working folder');
    expect(message).toContain('cannot be undone');
    expect(hub.deletes).toEqual([]);

    await click(buttonIn(dialog(), 'Delete')!);

    await vi.waitFor(() => expect(hub.deletes).toHaveLength(1));
    await vi.waitFor(() => expect(toast()?.textContent).toContain('Deleted "left-list" and its folder'));
    expect(buttonIn(toast(), 'Undo')).toBeUndefined();
  });

  it('deletes nothing when the dialog is cancelled', async () => {
    await show([session('left-list')]);
    await openMenu('left-list');
    await click(buttonIn(document.querySelector('.project-item-menu'), 'Delete…')!);
    await vi.waitFor(() => expect(dialog()).not.toBeNull());

    await click(buttonIn(dialog(), 'Cancel')!);

    expect(dialog()).toBeNull();
    expect(hub.deletes).toEqual([]);
  });
});

describe('the swipe', () => {
  it('to the left shows Delete behind the row, which deletes; a tap then closes it without opening the session', async () => {
    await show([session('notes', { shared: true }), session('other', { shared: true })]);

    await swipe(row('notes')!, 200, 80);
    const behind = q<HTMLButtonElement>('.project-item-swipe-delete');
    expect(behind.map(b => b.textContent)).toEqual(['Delete']);

    await click(behind[0]);
    await vi.waitFor(() => expect(hub.deletes.map(d => d.projectId)).toEqual(['Default/work/260929-chat-notes-abcd']));
    await vi.waitFor(() => expect(buttonIn(toast(), 'Undo')).toBeDefined());

    await swipe(row('other')!, 200, 80);
    await click(row('other')!);
    expect(q('.project-item-swipe-delete')).toHaveLength(0);
    expect(useAppStore.getState().selectedProject).toBeNull();
  });

  it('that is short, or mostly up and down (a scroll), shows nothing', async () => {
    await show([session('notes', { shared: true })]);

    await swipe(row('notes')!, 200, 170);
    expect(q('.project-item-swipe-delete')).toHaveLength(0);

    const el = row('notes')!;
    await act(async () => {
      for (const [type, x, y] of [['touchstart', 200, 10], ['touchmove', 190, 40], ['touchmove', 100, 80]] as const) {
        const event = new Event(type, { bubbles: true, cancelable: true });
        Object.defineProperty(event, 'touches', { value: [{ clientX: x, clientY: y }] });
        el.dispatchEvent(event);
      }
    });
    expect(q('.project-item-swipe-delete')).toHaveLength(0);
  });

  it('on a worktree asks first, as its menu does', async () => {
    await show([session('left-list')]);
    await swipe(row('left-list')!, 200, 80);
    await click(q<HTMLButtonElement>('.project-item-swipe-delete')[0]);
    await vi.waitFor(() => expect(dialog()).not.toBeNull());
    expect(hub.deletes).toEqual([]);
  });
});

describe('folding', () => {
  it('folds quiet sessions older than a week under "N older", one tap away, and never a live one', async () => {
    await show([
      session('recent', { updated: ago(2 * DAY) }),
      session('old', { updated: ago(8 * DAY) }),
      session('older', { updated: ago(40 * DAY), state: 'Error' }),
      session('old-but-running', { updated: ago(30 * DAY), state: 'Running' }),
      session('old-but-waiting', { updated: ago(30 * DAY), state: 'WaitingInput' }),
      session('old-but-idle', { updated: ago(30 * DAY), state: 'Idle' }),
    ]);

    expect(names()).toEqual(['recent', 'old-but-running', 'old-but-waiting', 'old-but-idle']);
    const toggle = document.querySelector<HTMLButtonElement>('.project-list-older')!;
    expect(toggle.textContent).toBe('2 older');

    await click(toggle);
    expect(names()).toEqual(['recent', 'old-but-running', 'old-but-waiting', 'old-but-idle', 'old', 'older']);
    expect(toggle.textContent).toBe('Hide older');
    expect(hub.deletes).toEqual([]);
  });

  it("folds a transient root's sessions after a day", async () => {
    await show([session('today', { updated: ago(DAY / 2) }), session('yesterday', { updated: ago(2 * DAY) })], work(true));
    expect(names()).toEqual(['today']);
    expect(document.querySelector('.project-list-older')?.textContent).toBe('1 older');
  });

  it('keeps an old session open that needs the user, or is open', async () => {
    await show([session('finished', { updated: ago(10 * DAY) }), session('opened', { updated: ago(10 * DAY) })]);
    expect(names()).toEqual([]);

    await act(async () => {
      hub.callbacks.onAttentionChanged?.([{ ProjectId: 'Default/work/260929-chat-finished-abcd', Kind: 'Finished', At: ago(10 * DAY), Text: 'done' } as never]);
      useAppStore.getState().selectProject('A', 'Default/work/260929-chat-opened-abcd');
    });

    await vi.waitFor(() => expect(names()).toEqual(['finished', 'opened']));
    expect(document.querySelector('.project-list-older')).toBeNull();
  });
});
