// @vitest-environment jsdom
/**
 * What is typed stays with what it was typed for (#240): the chat input is one project's, and an inbox
 * draft outlives a layout change across the phone breakpoint (768 px). Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem } from '../signalr/types';
import { FakeHub, project, root, connectServers } from '../test/fakeHub';
import { render, typeInto, keyDown, click, type Rendered } from '../test/render';
import { useAppStore } from '../store';
import { Shell } from './Shell';

vi.mock('../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
  isMaui: false,
  clearApiKey: () => {},
}));

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;

/** Opens a project from the sidebar's list. */
const open = (name: string) => click([...view.container.querySelectorAll<HTMLElement>('.project-item')]
  .find(el => el.querySelector('.project-name')?.textContent === name)!);
const chatInput = () => view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
const sendButton = () => view.container.querySelector<HTMLButtonElement>('.project-input-bar .btn-primary')!;
/** Crosses the phone breakpoint, as the Shell's media query does. */
const setPhone = (phone: boolean) => act(async () => useAppStore.getState().setIsMobile(phone));

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  hub = new FakeHub([
    project('p1', 'alpha', 'WaitingInput', '2026-09-24T12:00:00Z'),
    project('p2', 'beta', 'WaitingInput', '2026-09-24T11:00:00Z'),
    project('p3', 'gamma', 'WaitingPermission', '2026-09-24T10:00:00Z'),
  ], [root]);
  await connectServers({ A: hub });
  view = await render(<Shell />);
});

afterEach(() => view.unmount());

describe('switching projects', () => {
  it("clears the input, and B's Send does not carry A's text", async () => {
    await open('alpha');
    await typeInto(chatInput(), 'meant for alpha');

    await open('beta');
    expect(chatInput().value).toBe('');
    expect(sendButton().disabled).toBe(true);
    await keyDown(chatInput(), 'Enter');
    expect(hub.replies).toEqual([]);

    await typeInto(chatInput(), 'meant for beta');
    await click(sendButton());
    expect(hub.replies).toEqual([{ projectId: 'p2', text: 'meant for beta' }]);
  });
});

describe('an inbox draft', () => {
  const item = (projectId: string, kind: AttentionItem['Kind'], extra: Partial<AttentionItem> = {}): AttentionItem => ({
    ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: '2026-09-24T09:00:00Z', Text: `${projectId} ${kind}`, ...extra,
  });
  const itemEl = (projectId: string) => [...view.container.querySelectorAll<HTMLElement>('.inbox-item')]
    .find(el => el.querySelector('.inbox-item-name')?.textContent === projectId) ?? null;
  const replyField = (projectId: string) => itemEl(projectId)?.querySelector('textarea') ?? null;
  const denyField = (projectId: string) => itemEl(projectId)?.querySelector<HTMLInputElement>('.permission-deny-message') ?? null;

  beforeEach(async () => {
    await act(async () => hub.callbacks.onAttentionChanged?.([
      item('p2', 'Question'),
      item('p3', 'Permission', {
        Permission: { RequestId: 'r3', ToolName: 'Bash', Input: {}, Summary: 'Bash: git push', RequestedAt: '2026-09-24T09:00:00Z' },
      }),
    ]));
  });

  it('keeps its place and its text across 768 px on the home screen', async () => {
    const reply = replyField('p2')!;
    await typeInto(reply, 'half a reply');

    // The laptop's pane becomes the phone's home, and back: the same field, never remounted
    await setPhone(true);
    expect(view.container.querySelector('.shell-mobile-home .inbox-screen')).not.toBeNull();
    expect(replyField('p2')).toBe(reply);
    expect(reply.value).toBe('half a reply');

    await setPhone(false);
    expect(view.container.querySelector('.shell-sidebar .inbox-pane')).not.toBeNull();
    expect(replyField('p2')).toBe(reply);
    expect(reply.value).toBe('half a reply');
  });

  it('comes back with the pane, after a phone showed the open project without it', async () => {
    await open('alpha');
    await typeInto(replyField('p2')!, 'half a reply');
    await typeInto(denyField('p3')!, 'not on Fridays');

    await setPhone(true);
    expect(view.container.querySelector('.inbox')).toBeNull();
    await setPhone(false);

    expect(replyField('p2')?.value).toBe('half a reply');
    expect(denyField('p3')?.value).toBe('not on Fridays');
    await click([...itemEl('p3')!.querySelectorAll('button')].find(b => b.textContent === 'Deny with message')!);
    expect(hub.decisions).toEqual([{ projectId: 'p3', requestId: 'r3', decision: { Allow: false, Message: 'not on Fridays' } }]);
  });

  it('is sent once, and cleared', async () => {
    await typeInto(replyField('p2')!, 'Use SQLite');
    await click([...itemEl('p2')!.querySelectorAll('button')].find(b => b.textContent === 'Send')!);
    expect(hub.replies).toEqual([{ projectId: 'p2', text: 'Use SQLite' }]);
    expect(useAppStore.getState().inboxDrafts).toEqual({});
  });
});
