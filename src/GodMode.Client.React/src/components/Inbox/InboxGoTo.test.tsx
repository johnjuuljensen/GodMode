// @vitest-environment jsdom
/**
 * An inbox item's Go to (#440): on every kind, it opens the project, in the list layout, in tile mode and on the phone,
 * and opening a project marks nothing seen. And the user who writes in the project's own composer instead of the
 * item's reply box clears the item as the reply would: both are the hub's ReplyAndResume, and the item goes with the
 * list the server pushes after it, in the main window and in a profile's (#340). Renders the Shell on the real store,
 * against a fake server that drops the item a reply or a Mark seen sees, as ProjectLifecycle.SendInputAsync does.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, AttentionKind, PendingQuestion } from '../../signalr/types';
import { FakeHub, project, root, connectServers, flush, status } from '../../test/fakeHub';
import { render, typeInto, click, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Shell } from '../Shell';

/** What the app answers window.info with: the main window, or a profile's. */
const host = vi.hoisted(() => ({ window: { Profile: null as string | null, CanOpenWindows: false } }));

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  windowInfo: async () => host.window,
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

// The window's (max-width: 768px): a phone when set
let phone = false;
const otherMedia = window.matchMedia;
window.matchMedia = (query: string) => query !== '(max-width: 768px)' ? otherMedia(query) : ({
  matches: phone, media: query, onchange: null,
  addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
}) as unknown as MediaQueryList;

/**
 * The server, as far as attention goes: its list, and what a reply and a Mark seen do to it. A reply is the user having
 * seen the project (SeenAt), so its Finished, Review, plain-text Question and Error leave; an Escalation leaves with the
 * user's write; a pending request is answered by it. Either way the project runs, and the list without it is pushed.
 */
class AttentionServer extends FakeHub {
  attention: AttentionItem[] = [];
  push(items: AttentionItem[]) {
    this.attention = items;
    this.callbacks.onAttentionChanged?.(items);
  }
  private sees(projectId: string) {
    this.push(this.attention.filter(i => i.ProjectId !== projectId));
  }
  async getAttention() { await super.getAttention(); return this.attention; }
  async replyAndResume(projectId: string, text: string) {
    await super.replyAndResume(projectId, text);
    this.callbacks.onStatusChanged?.(projectId, status(projectId, 'Running'));
    this.sees(projectId);
  }
  async markSeen(projectId: string) {
    await super.markSeen(projectId);
    this.sees(projectId);
  }
}

const asking: PendingQuestion = {
  RequestId: 'q1', RequestedAt: '2026-10-03T09:00:00Z',
  Questions: [{ Question: 'Which way?', Options: [{ Label: 'Left' }, { Label: 'Right' }], MultiSelect: false }],
};

const item = (projectId: string, kind: AttentionKind, extra: Partial<AttentionItem> = {}): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: '2026-10-03T09:00:00Z', Text: `${projectId} ${kind}`, ...extra,
});

/** One item of each kind, each its own project's. */
const everyKind: AttentionItem[] = [
  item('p-permission', 'Permission', { Permission: { RequestId: 'r1', ToolName: 'Bash', Summary: 'Bash: git push', RequestedAt: '2026-10-03T09:00:00Z' } }),
  item('p-asking', 'Question', { Question: asking }),
  item('p-question', 'Question'),
  item('p-error', 'Error'),
  item('p-failed', 'Error', { CreateFailed: true }),
  item('p-escalation', 'Escalation', { PullRequestUrl: 'https://example.test/issues/1' }),
  item('p-review', 'Review', { PullRequestUrl: 'https://example.test/pr/1' }),
  item('p-finished', 'Finished', { PullRequestUrl: 'https://example.test/pr/2' }),
];

const initialState = useAppStore.getState();
let hub: AttentionServer;
let view: Rendered;

const itemEl = (projectId: string) => [...view.container.querySelectorAll<HTMLElement>('.inbox-item')]
  .find(el => el.querySelector('.inbox-item-name')?.textContent === projectId) ?? null;
const buttonIn = (el: HTMLElement, label: string) => [...el.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent === label);
const goTo = (projectId: string) => click(buttonIn(itemEl(projectId)!, 'Go to')!);
const shownProject = () => view.container.querySelector('.project-view .project-header-name')?.textContent ?? null;
const inbox = () => view.container.querySelectorAll('.inbox-item').length;
const composer = () => view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
const composerSend = () => view.container.querySelector<HTMLButtonElement>('.project-input-bar .btn-primary')!;

async function start(items: AttentionItem[], window: { Profile: string | null; CanOpenWindows: boolean } = { Profile: null, CanOpenWindows: false }) {
  host.window = window;
  hub = new AttentionServer(items.map(i => ({ ...project(i.ProjectId, i.ProjectName, 'Idle', i.Since), ProfileName: i.Profile ?? 'Default' })), [root]);
  hub.attention = items;
  await useAppStore.getState().loadWindow();
  await connectServers({ A: hub });
  await act(async () => { hub.push(items); });
  view = await render(<Shell />);
  await flush();
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  phone = false;
});

afterEach(() => view.unmount());

describe('Go to', () => {
  it('is on every kind, beside Open PR and Mark seen', async () => {
    await start(everyKind);
    for (const { ProjectId } of everyKind) {
      const actions = itemEl(ProjectId)!.querySelector('.inbox-item-actions');
      expect(actions && buttonIn(actions as HTMLElement, 'Go to'), ProjectId).toBeTruthy();
    }
    const finished = itemEl('p-finished')!.querySelector<HTMLElement>('.inbox-item-actions')!;
    expect([...finished.querySelectorAll('a, button')].map(e => e.textContent)).toEqual(['Go to', 'Open PR', 'Mark seen']);
  });

  it("selects the item's project, and the list layout shows it beside the inbox", async () => {
    await start(everyKind);
    await goTo('p-review');
    expect(useAppStore.getState().selectedProject).toEqual({ serverId: 'A', projectId: 'p-review' });
    expect(shownProject()).toBe('p-review');
    expect(itemEl('p-review')).not.toBeNull();
  });

  it('shows the project in place of the tiles, in tile mode', async () => {
    await start(everyKind);
    await act(async () => useAppStore.getState().setTileView(true));
    expect(view.container.querySelector('.tile-grid')).not.toBeNull();

    await goTo('p-permission');
    expect(shownProject()).toBe('p-permission');
    expect(view.container.querySelector('.tile-grid')).toBeNull();
  });

  it("shows the project's screen in place of the inbox, on the phone", async () => {
    phone = true;
    await start(everyKind);
    expect(view.container.querySelector('.shell-mobile-home .inbox-screen')).not.toBeNull();

    await goTo('p-asking');
    expect(shownProject()).toBe('p-asking');
    expect(view.container.querySelector('.inbox')).toBeNull();
    expect(view.container.querySelector('.page-back-bar')).not.toBeNull();
  });

  it('marks nothing seen: opening a project and reading it leaves its item (#440)', async () => {
    await start(everyKind);
    await goTo('p-finished');
    expect(hub.seen).toEqual([]);
    expect(itemEl('p-finished')).not.toBeNull();
    expect(inbox()).toBe(everyKind.length);
  });
});

describe("a send in the project's own composer (#440)", () => {
  // The kinds a reply clears by the user having seen them, or by answering them
  const answered: AttentionKind[] = ['Finished', 'Review', 'Question', 'Error', 'Escalation', 'Permission'];

  it.each(answered)('clears a %s item, as a reply from the item does, and no other', async (kind) => {
    const other = item('p-other', 'Finished', { Since: '2026-10-03T08:00:00Z' });
    await start([other, everyKind.find(i => i.Kind === kind && !i.CreateFailed && !i.Question)!]);
    const id = useAppStore.getState().attention[1].ProjectId;
    await goTo(id);

    await typeInto(composer(), 'Carry on');
    await click(composerSend());

    expect(hub.replies).toEqual([{ projectId: id, text: 'Carry on' }]);
    expect(itemEl(id)).toBeNull();
    expect(itemEl('p-other')).not.toBeNull();
    expect(useAppStore.getState().attention.map(i => i.ProjectId)).toEqual(['p-other']);
  });

  it("clears it in a profile's window too (#340)", async () => {
    const work = (i: AttentionItem) => ({ ...i, Profile: 'Work' });
    await start([work(item('p-finished', 'Finished')), work(item('p-other', 'Question', { Since: '2026-10-03T08:00:00Z' }))], { Profile: 'Work', CanOpenWindows: false });
    expect(useAppStore.getState().lockedProfile).toBe('Work');
    await goTo('p-finished');

    await typeInto(composer(), 'Ship it');
    await click(composerSend());

    expect(itemEl('p-finished')).toBeNull();
    expect(itemEl('p-other')).not.toBeNull();
  });
});
