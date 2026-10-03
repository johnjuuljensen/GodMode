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
import { ConfirmDialog } from '../ConfirmDialog';
import { dismissConfirm } from '../../confirmDialog';
import { dismissToast } from '../../toast';
import { join } from 'node:path';
import { classesIn, cuttingRules, longRequest, ruleOf, textsOf } from '../../test/readable';

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
  // What each request would run: Allow waits for it (#234)
  hubA.details = Object.fromEntries(['r1', 'r2'].map(id => [id, { RequestId: id, Detail: `echo ${id}`, DetailTruncated: false }]));
  await act(async () => {
    hubA.callbacks.onAttentionChanged?.([
      item('p1', 'Question', '2026-09-24T10:00:00Z'),
      item('p2', 'Permission', '2026-09-24T11:00:00Z', {
        Permission: { RequestId: 'r1', ToolName: 'Bash', Summary: 'Bash: git push', RequestedAt: '2026-09-24T11:00:00Z' },
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

  it('marks a question in plain text seen, beside its reply box (#426)', async () => {
    const el = itemEl('p1', 'Server A');
    expect(el.querySelector('textarea')).not.toBeNull();
    await click(button(el, 'Mark seen'));
    expect(hubA.seen).toEqual(['p1']);
  });

  it('marks an error seen (#426)', async () => {
    await act(async () => hubA.callbacks.onAttentionChanged?.([item('p3', 'Error', '2026-09-24T12:00:00Z')]));
    await click(button(itemEl('p3', 'Server A'), 'Mark seen'));
    expect(hubA.seen).toEqual(['p3']);
  });

  it('offers no Mark seen on a pending AskUserQuestion, which only an answer clears (#426)', async () => {
    await act(async () => hubA.callbacks.onAttentionChanged?.([item('p3', 'Question', '2026-09-24T12:00:00Z', {
      Question: {
        RequestId: 'q1', RequestedAt: '2026-09-24T12:00:00Z',
        Questions: [{ Question: 'Which color?', Header: null, Options: [{ Label: 'Red', Description: null }], MultiSelect: false }],
      },
    })]));
    const el = itemEl('p3', 'Server A');
    expect(button(el, 'Red')).toBeDefined();
    expect(button(el, 'Mark seen')).toBeUndefined();
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
    Permission: { RequestId: requestId, ToolName: 'Bash', Summary: `Bash: ${requestId}`, RequestedAt: since },
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

describe('a create that failed (#448)', () => {
  const dialog = () => document.querySelector('.confirm-dialog');

  beforeEach(async () => {
    view.unmount();
    await act(async () => hubA.callbacks.onAttentionChanged?.([
      item('p3', 'Error', '2026-09-24T12:00:00Z', { Text: 'The create script failed: no worktree', CreateFailed: true }),
    ]));
    view = await render(<><Inbox variant="screen" /><ConfirmDialog /></>);
  });

  afterEach(() => act(() => { dismissToast(); dismissConfirm(); }));

  it('takes no reply, says why, and offers its delete', async () => {
    const el = itemEl('p3', 'Server A');
    expect(el.querySelector('.inbox-item-text')?.textContent).toBe('The create script failed: no worktree');
    expect(el.querySelector('textarea')).toBeNull();

    await click(button(el, 'Delete'));
    await vi.waitFor(() => expect(dialog()).not.toBeNull());
    await click([...dialog()!.querySelectorAll('button')].find(b => b.textContent === 'Delete')!);

    await vi.waitFor(() => expect(hubA.deletes).toEqual([{ projectId: 'p3', force: false }]));
    expect(hubA.replies).toEqual([]);
  });
});

describe('a pending question, read in full (#454)', () => {
  const css = join(import.meta.dirname, 'Inbox.css');
  const option = (el: HTMLElement, label: string) => [...el.querySelectorAll<HTMLButtonElement>('.inbox-option')]
    .find(b => b.querySelector('.inbox-option-label')?.textContent === label)!;
  const asking = () => itemEl('p5', 'Server A');

  beforeEach(async () => {
    await act(async () => hubA.callbacks.onAttentionChanged?.([item('p5', 'Question', '2026-10-03T12:00:00Z', {
      Text: longRequest.Questions.map(q => q.Question).join('\n'), Question: longRequest,
    })]));
  });

  it('shows every word of every question, header, option and description', () => {
    const shown = asking().textContent!;
    for (const text of textsOf(longRequest)) expect(shown).toContain(text);
    expect(asking().querySelectorAll('.inbox-question')).toHaveLength(3);
  });

  it('has no rule that cuts its text off', () => {
    expect(cuttingRules(css, classesIn(asking().querySelector('.inbox-questions')!))).toEqual([]);
  });

  it('shows a description as text under its label, not in a tooltip, at a readable size and contrast', () => {
    const description = longRequest.Questions[0].Options[0].Description!;
    expect(option(asking(), 'Yes, repair now').querySelector('.inbox-option-desc')?.textContent).toBe(description);
    expect([...asking().querySelectorAll('[title]')].map(el => el.getAttribute('title'))).not.toContain(description);
    const desc = ruleOf(css, '.inbox-option-desc');
    expect(Number(/font-size:\s*([\d.]+)px/.exec(desc)?.[1])).toBeGreaterThanOrEqual(12);
    expect(desc).toContain('var(--text-secondary)');
  });

  it('takes one answer per question, a multi-select as one, and sends them together', async () => {
    const send = button(asking(), 'Send answers');
    expect(asking().querySelectorAll('.inbox-question-multi')).toHaveLength(1);
    await click(option(asking(), 'No, leave it'));
    await click(option(asking(), 'Yes, repair now'));
    await click(option(asking(), 'The leak'));
    await click(option(asking(), 'The crash'));
    expect(send.disabled).toBe(true);
    await click(option(asking(), 'Wait'));
    await click(send);
    expect(hubA.answers).toEqual([{ projectId: 'p5', requestId: 'long', answers: {
      [longRequest.Questions[0].Question]: 'Yes, repair now',
      [longRequest.Questions[1].Question]: 'The crash, The leak',
      [longRequest.Questions[2].Question]: 'Wait',
    } }]);
    expect(hubA.replies).toEqual([]);
  });

  it('answers a single question with a tap on its option, its description shown', async () => {
    const [first] = longRequest.Questions;
    await act(async () => hubA.callbacks.onAttentionChanged?.([item('p5', 'Question', '2026-10-03T12:05:00Z', {
      Text: first.Question, Question: { ...longRequest, Questions: [first] },
    })]));
    expect(option(asking(), 'No, leave it').textContent).toContain(first.Options[1].Description!);
    await click(option(asking(), 'No, leave it'));
    expect(hubA.replies).toEqual([{ projectId: 'p5', text: 'No, leave it' }]);
  });
});
