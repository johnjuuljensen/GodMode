// @vitest-environment jsdom
/**
 * What is typed stays with what it was typed for (#240): the chat input is one project's, and an inbox
 * draft outlives a layout change across the phone breakpoint (768 px), but not its permission request
 * (#218). The phone's layouts (#218): its home is the inbox, tile mode or not, and a wide screen has no
 * list screen. Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, PendingQuestion } from '../signalr/types';
import { FakeHub, project, root, connectServers } from '../test/fakeHub';
import { render, typeInto, keyDown, click, pointerClick, type Rendered } from '../test/render';
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

// The window's (max-width: 768px), which a test sets as a resized window changes it; every other query is as before
let phone = false;
type PhoneListener = (e: { matches: boolean }) => void;
const phoneListeners = new Set<PhoneListener>();
const otherMedia = window.matchMedia;
window.matchMedia = (query: string) => query !== '(max-width: 768px)' ? otherMedia(query) : ({
  matches: phone, media: query, onchange: null,
  addEventListener: (_type: string, listener: PhoneListener) => { phoneListeners.add(listener); },
  removeEventListener: (_type: string, listener: PhoneListener) => { phoneListeners.delete(listener); },
  addListener: () => {}, removeListener: () => {}, dispatchEvent: () => false,
}) as unknown as MediaQueryList;

const asking: PendingQuestion = {
  RequestId: 'q4',
  RequestedAt: '2026-09-24T09:00:00Z',
  Questions: [{ Question: 'Which way?', Options: [{ Label: 'Left' }, { Label: 'Right' }], MultiSelect: false }],
};

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;

/** Opens a project from the sidebar's list. */
const open = (name: string) => click([...view.container.querySelectorAll<HTMLElement>('.project-item')]
  .find(el => el.querySelector('.project-name')?.textContent === name)!);
const chatInput = () => view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
const sendButton = () => view.container.querySelector<HTMLButtonElement>('.project-input-bar .btn-primary')!;
/** Crosses the phone breakpoint, as a resized window does: the Shell's media query changes. */
const setPhone = (next: boolean) => act(async () => {
  phone = next;
  phoneListeners.forEach(listener => listener({ matches: next }));
});

const item = (projectId: string, kind: AttentionItem['Kind'], extra: Partial<AttentionItem> = {}): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: '2026-09-24T09:00:00Z', Text: `${projectId} ${kind}`, ...extra,
});
const itemEl = (projectId: string) => [...view.container.querySelectorAll<HTMLElement>('.inbox-item')]
  .find(el => el.querySelector('.inbox-item-name')?.textContent === projectId) ?? null;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  phone = false;
  hub = new FakeHub([
    project('p1', 'alpha', 'WaitingInput', '2026-09-24T12:00:00Z'),
    project('p2', 'beta', 'WaitingInput', '2026-09-24T11:00:00Z'),
    project('p3', 'gamma', 'WaitingPermission', '2026-09-24T10:00:00Z'),
    { ...project('p4', 'delta', 'WaitingInput', '2026-09-24T09:00:00Z'), PendingQuestion: asking },
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
  const replyField = (projectId: string) => itemEl(projectId)?.querySelector('textarea') ?? null;
  const denyField = (projectId: string) => itemEl(projectId)?.querySelector<HTMLInputElement>('.permission-deny-message') ?? null;
  const permission = (requestId: string, since: string) => item('p3', 'Permission', {
    Since: since,
    Permission: { RequestId: requestId, ToolName: 'Bash', Summary: 'Bash: git push', RequestedAt: since },
  });

  beforeEach(async () => {
    await act(async () => hub.callbacks.onAttentionChanged?.([item('p2', 'Question'), permission('r3', '2026-09-24T09:00:00Z')]));
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

  it("is its request's: one answered elsewhere does not come back on the next (#218)", async () => {
    await open('alpha');
    await typeInto(denyField('p3')!, 'not on Fridays');

    // While the phone shows the project without the inbox, r3 is answered elsewhere and claude asks again
    await setPhone(true);
    expect(view.container.querySelector('.inbox')).toBeNull();
    await act(async () => hub.callbacks.onAttentionChanged?.([item('p2', 'Question'), permission('r4', '2026-09-24T09:05:00Z')]));
    await setPhone(false);

    expect(denyField('p3')?.value).toBe('');
    const deny = [...itemEl('p3')!.querySelectorAll('button')].find(b => b.textContent?.startsWith('Deny'))!;
    expect(deny.textContent).toBe('Deny');
    await click(deny);
    expect(hub.decisions).toEqual([{ projectId: 'p3', requestId: 'r4', decision: { Allow: false, Message: null } }]);
  });

  it('is sent once, and cleared', async () => {
    await typeInto(replyField('p2')!, 'Use SQLite');
    await click([...itemEl('p2')!.querySelectorAll('button')].find(b => b.textContent === 'Send')!);
    expect(hub.replies).toEqual([{ projectId: 'p2', text: 'Use SQLite' }]);
    expect(useAppStore.getState().inboxDrafts).toEqual({});
  });
});

describe('a question opened from the inbox header (#218)', () => {
  it('takes its keys: the header, which stays beside the project, lets go of the focus', async () => {
    await act(async () => hub.callbacks.onAttentionChanged?.([item('p4', 'Question', { Question: asking })]));
    await pointerClick(itemEl('p4')!.querySelector<HTMLElement>('.inbox-item-header')!);
    expect(view.container.querySelector('.project-view .question-prompt')).not.toBeNull();
    expect(itemEl('p4')).not.toBeNull();

    await keyDown(document.activeElement, '2');
    expect(hub.answers).toEqual([{ projectId: 'p4', requestId: 'q4', answers: { 'Which way?': 'Right' } }]);
  });
});

describe("the phone's layouts (#218)", () => {
  const tileToggle = () => view.container.querySelector<HTMLButtonElement>('button[title="Tile view"], button[title="List view"]');

  it('has its inbox in tile mode too: no toggle there, and widening brings the tiles back', async () => {
    await click(tileToggle()!);
    expect(view.container.querySelector('.tile-grid')).not.toBeNull();

    await setPhone(true);
    expect(view.container.querySelector('.tile-grid')).toBeNull();
    expect(view.container.querySelector('.shell-mobile-home .inbox-screen')).not.toBeNull();
    expect(tileToggle()).toBeNull();

    await setPhone(false);
    expect(view.container.querySelector('.tile-grid')).not.toBeNull();
    expect(view.container.querySelector('.inbox-pane')).not.toBeNull();
  });

  it('leave #/projects behind when the window widens: a wide screen shows the list beside the inbox', async () => {
    await setPhone(true);
    await click([...view.container.querySelectorAll<HTMLElement>('.home-tab')].find(b => b.textContent === 'Projects')!);
    expect(location.hash).toBe('#/projects');

    const popped = new Promise(resolve => window.addEventListener('popstate', resolve, { once: true }));
    await setPhone(false);
    // Home is the entry before: the router steps back to it rather than stacking a copy
    await act(async () => { await popped; });
    expect(useAppStore.getState().homeView).toBe('inbox');
    expect(location.hash).toBe('#/');
  });
});

describe("the phone's project view (#221)", () => {
  const indicator = () => view.container.querySelector<HTMLButtonElement>('.page-back-bar .project-connection');

  it("shows its server's connection while it is lost, retries on a tap, and goes when it is back", async () => {
    await setPhone(true);
    await click([...view.container.querySelectorAll<HTMLElement>('.home-tab')].find(b => b.textContent === 'Projects')!);
    await open('alpha');
    expect(view.container.querySelector('.page-back-bar')).not.toBeNull();
    expect(indicator()).toBeNull();

    await act(() => hub.drop());
    expect(indicator()?.textContent).toBe('Reconnecting…');
    await click(indicator()!);
    expect(hub.calls.retryNow).toBe(1);

    await act(() => hub.reconnect());
    expect(indicator()).toBeNull();
  });

  it('is not on a wide screen, where the sidebar has its own', async () => {
    await open('alpha');
    await act(() => hub.drop());
    expect(view.container.querySelector('.project-connection')).toBeNull();
  });
});
