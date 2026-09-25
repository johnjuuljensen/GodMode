// @vitest-environment jsdom
/**
 * Answering from the inbox (#172): each kind's controls call the hub for the item's own server and
 * project, and what an item held for one need is not the next one's (#218). Renders the Inbox on the
 * real store with two servers whose project IDs overlap.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import type { AttentionItem } from '../../signalr/types';
import { FakeHub, connectServers } from '../../test/fakeHub';
import { render, typeInto, click, type Rendered } from '../../test/render';
import { useAppStore, projectKey } from '../../store';
import { Inbox } from './Inbox';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

const item = (projectId: string, kind: AttentionItem['Kind'], since: string, extra: Partial<AttentionItem> = {}): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: since, Text: `${projectId} ${kind}`, ...extra,
});

const initialState = useAppStore.getState();
let hubA: FakeHub;
let hubB: FakeHub;
let view: Rendered;

/** The rendered item whose header names `name` and `server`. */
const itemEl = (name: string, server: string) => [...view.container.querySelectorAll<HTMLElement>('.inbox-item')]
  .find(el => el.querySelector('.inbox-item-name')?.textContent === name && el.querySelector('.inbox-item-meta')?.textContent?.includes(server))!;
const button = (el: HTMLElement, label: string) => [...el.querySelectorAll('button')].find(b => b.textContent === label)!;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hubA = new FakeHub([], []);
  hubB = new FakeHub([], []);
  await connectServers({ A: hubA, B: hubB });
  await act(async () => {
    hubA.callbacks.onAttentionChanged?.([
      item('p1', 'Question', '2026-09-24T10:00:00Z'),
      item('p2', 'Permission', '2026-09-24T11:00:00Z', {
        Permission: { RequestId: 'r1', ToolName: 'Bash', Input: {}, Summary: 'Bash: git push', RequestedAt: '2026-09-24T11:00:00Z' },
      }),
    ]);
    hubB.callbacks.onAttentionChanged?.([item('p1', 'Finished', '2026-09-24T09:00:00Z', { PullRequestUrl: 'https://example.test/pr/1' })]);
  });
  view = await render(<Inbox variant="screen" />);
});

afterEach(() => view.unmount());

describe('the inbox', () => {
  it('lists every server\'s items oldest first, with the count', () => {
    const names = [...view.container.querySelectorAll('.inbox-item')].map(el =>
      `${el.querySelector('.inbox-item-name')?.textContent}@${el.querySelector('.inbox-item-meta')?.textContent?.split(' · ')[0]}`);
    expect(names).toEqual(['p1@Server B', 'p1@Server A', 'p2@Server A']);
    expect(view.container.querySelector('.inbox-count')?.textContent).toBe('3');
  });

  it('sends a reply to the item\'s own server', async () => {
    const el = itemEl('p1', 'Server A');
    await typeInto(el.querySelector('textarea')!, 'Use SQLite');
    await click(button(el, 'Send'));
    expect(hubA.replies).toEqual([{ projectId: 'p1', text: 'Use SQLite' }]);
    expect(hubB.replies).toEqual([]);
  });

  it('allows a permission', async () => {
    await click(button(itemEl('p2', 'Server A'), 'Allow'));
    expect(hubA.decisions).toEqual([{ projectId: 'p2', requestId: 'r1', decision: { Allow: true, Message: null } }]);
  });

  it('denies a permission with the message typed', async () => {
    const el = itemEl('p2', 'Server A');
    await typeInto(el.querySelector<HTMLInputElement>('.permission-deny-message')!, 'Not on Fridays');
    await click(button(el, 'Deny with message'));
    expect(hubA.decisions).toEqual([{ projectId: 'p2', requestId: 'r1', decision: { Allow: false, Message: 'Not on Fridays' } }]);
  });

  it('marks a finished item seen on its server, and links its pull request', async () => {
    const el = itemEl('p1', 'Server B');
    expect(el.querySelector('a')?.getAttribute('href')).toBe('https://example.test/pr/1');
    await click(button(el, 'Mark seen'));
    expect(hubB.seen).toEqual(['p1']);
    expect(hubA.seen).toEqual([]);
  });

  it('opens the project from the header', async () => {
    await click(itemEl('p1', 'Server B').querySelector<HTMLElement>('.inbox-item-header')!);
    expect(useAppStore.getState().selectedProject).toEqual({ serverId: 'B', projectId: 'p1' });
  });

  it('opens on the item a tapped notification names, from another screen, and scrolls to it again on a second tap', async () => {
    const scrolled: Element[] = [];
    HTMLElement.prototype.scrollIntoView = function (this: HTMLElement) { scrolled.push(this); };
    try {
      useAppStore.getState().selectProject('B', 'p1');
      await act(async () => useAppStore.getState().openInboxItem('A', 'p1'));

      const state = useAppStore.getState();
      expect([state.selectedProject, state.activePage, state.homeView]).toEqual([null, null, 'inbox']);
      // Server B has a p1 too: only server A's is the one opened
      const focused = [...view.container.querySelectorAll('.inbox-item-focused')];
      expect(focused).toEqual([itemEl('p1', 'Server A')]);
      expect(scrolled).toEqual(focused);

      await act(async () => useAppStore.getState().openInboxItem('A', 'p1'));
      expect(scrolled).toEqual([...focused, ...focused]);

      // Another server's older item lands above it, as servers connect after a cold start: back to the item
      await act(async () => hubB.callbacks.onAttentionChanged?.([
        item('p1', 'Finished', '2026-09-24T09:00:00Z', { PullRequestUrl: 'https://example.test/pr/1' }),
        item('p0', 'Question', '2026-09-24T08:00:00Z'),
      ]));
      expect(scrolled).toEqual([...focused, ...focused, itemEl('p1', 'Server A')]);
    } finally {
      delete (HTMLElement.prototype as Partial<HTMLElement>).scrollIntoView;
    }
  });

  // The store once resolved a call to a missing hub as done, and the item cleared what was typed (#239)
  it('keeps a reply, and says why, when its server has left the list', async () => {
    const el = itemEl('p1', 'Server B');
    await act(async () => useAppStore.setState(s => ({ serverConnections: s.serverConnections.filter(c => c.serverInfo.Id !== 'B') })));
    expect(el.isConnected).toBe(true);
    await typeInto(el.querySelector('textarea')!, 'Ship it');
    await click(button(el, 'Send'));

    expect(el.querySelector('.inbox-item-error')?.textContent).toBe("This project's server is no longer in the server list");
    expect(el.querySelector('textarea')!.value).toBe('Ship it');
    expect(useAppStore.getState().inboxDrafts[projectKey('B', 'p1')]).toEqual({ reply: 'Ship it', deny: null });
    expect(hubB.replies).toEqual([]);
  });

  it('says so when nothing needs the user', async () => {
    await act(async () => { hubA.callbacks.onAttentionChanged?.([]); hubB.callbacks.onAttentionChanged?.([]); });
    expect(view.container.textContent).toContain('Nothing needs you.');
  });
});

describe('the next need of a project (#218)', () => {
  const permission = (requestId: string, since: string) => item('p2', 'Permission', since, {
    Permission: { RequestId: requestId, ToolName: 'Bash', Input: {}, Summary: `Bash: ${requestId}`, RequestedAt: since },
  });
  /** Server A's list again, with p2 as given: p1's question stays. */
  const listA = (p2: AttentionItem) => act(async () => hubA.callbacks.onAttentionChanged?.([item('p1', 'Question', '2026-09-24T10:00:00Z'), p2]));
  const p2 = () => itemEl('p2', 'Server A');

  it("is answerable at once: a new request's card is not the last one's, still sending", async () => {
    // r1's answer is on its way when claude moves on to r2
    vi.spyOn(hubA, 'respondToPermission').mockImplementationOnce(() => new Promise(() => {}));
    await click(button(p2(), 'Allow'));
    expect(button(p2(), 'Allow').disabled).toBe(true);

    await listA(permission('r2', '2026-09-24T11:05:00Z'));
    expect(button(p2(), 'Allow').disabled).toBe(false);
    await click(button(p2(), 'Allow'));
    expect(hubA.decisions).toEqual([{ projectId: 'p2', requestId: 'r2', decision: { Allow: true, Message: null } }]);
  });

  it("shows no error of the last one's", async () => {
    vi.spyOn(hubA, 'respondToPermission').mockRejectedValueOnce(new Error('No request r1 is pending'));
    await click(button(p2(), 'Deny'));
    expect(p2().querySelector('.inbox-item-error')?.textContent).toBe('No request r1 is pending');

    await listA(item('p2', 'Finished', '2026-09-24T11:05:00Z'));
    expect(p2().querySelector('.inbox-item-kind')?.textContent).toBe('Finished');
    expect(p2().querySelector('.inbox-item-error')).toBeNull();
  });
});
