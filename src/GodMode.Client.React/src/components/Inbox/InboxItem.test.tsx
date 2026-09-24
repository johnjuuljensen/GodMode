// @vitest-environment jsdom
/**
 * Answering from the inbox (#172): each kind's controls call the hub for the item's own server and
 * project. Renders the Inbox on the real store with two servers whose project IDs overlap.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import type { AttentionItem, PermissionDecision } from '../../signalr/types';
import { FakeHub, connectServers } from '../../test/fakeHub';
import { render, typeInto, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Inbox } from './Inbox';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

class InboxHub extends FakeHub {
  decisions: { projectId: string; requestId: string; decision: PermissionDecision }[] = [];
  seen: string[] = [];
  async respondToPermission(projectId: string, requestId: string, decision: PermissionDecision) {
    this.decisions.push({ projectId, requestId, decision });
  }
  async markSeen(projectId: string) { this.seen.push(projectId); }
}

const item = (projectId: string, kind: AttentionItem['Kind'], since: string, extra: Partial<AttentionItem> = {}): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: since, Text: `${projectId} ${kind}`, ...extra,
});

const initialState = useAppStore.getState();
let hubA: InboxHub;
let hubB: InboxHub;
let view: Rendered;

/** The rendered item whose header names `name` and `server`. */
const itemEl = (name: string, server: string) => [...view.container.querySelectorAll<HTMLElement>('.inbox-item')]
  .find(el => el.querySelector('.inbox-item-name')?.textContent === name && el.querySelector('.inbox-item-meta')?.textContent?.includes(server))!;
const button = (el: HTMLElement, label: string) => [...el.querySelectorAll('button')].find(b => b.textContent === label)!;
const click = (el: HTMLElement) => act(async () => { el.click(); });

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hubA = new InboxHub([], []);
  hubB = new InboxHub([], []);
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

  it('says so when nothing needs the user', async () => {
    await act(async () => { hubA.callbacks.onAttentionChanged?.([]); hubB.callbacks.onAttentionChanged?.([]); });
    expect(view.container.textContent).toContain('Nothing needs you.');
  });
});
