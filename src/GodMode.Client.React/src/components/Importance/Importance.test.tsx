// @vitest-environment jsdom
/**
 * How much a session may interrupt the user (#438): set from its row's menu and its header, kept by the server, which
 * pushes it back to every client; an important session is starred in the list and the inbox, a quiet one tagged. And
 * this device's own sound switch, in the settings, which the app keeps. Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, ProjectRootInfo, ProjectSummary } from '../../signalr/types';
import { FakeHub, connectServers, project } from '../../test/fakeHub';
import { render, click, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { Shell } from '../Shell';
import { Inbox } from '../Inbox/Inbox';
import { AppSettings } from '../AppSettings';
import { ImportancePicker } from './Importance';

/** What the app keeps as this device's sound switch, and every set it was asked for. */
const host = vi.hoisted(() => ({ sound: true, sets: [] as boolean[], answers: true }));

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  subscribeAttentionLinks: () => () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
  attentionSound: async () => { if (!host.answers) throw new Error('no app'); return host.sound; },
  setAttentionSound: async (enabled: boolean) => { host.sets.push(enabled); host.sound = enabled; return enabled; },
}));
vi.mock('../../services/voice', () => ({ useVoice: () => null }));

const work: ProjectRootInfo = { Name: 'work', ProfileName: 'Default', Actions: [{ Name: 'chat', AllowSkipPermissions: false, Session: true, Transient: false }] };
const ID = 'Default/work/261003-chat-notes-abcd';
const session = (extra: Partial<ProjectSummary> = {}): ProjectSummary => ({
  ...project(ID, 'notes', 'Idle', new Date().toISOString()), ActionName: 'chat', Kind: 'chat', ...extra,
});

const initialState = useAppStore.getState();
let view: Rendered | undefined;
let hub: FakeHub;

const q = <T extends Element = HTMLElement>(sel: string) => [...document.querySelectorAll<T>(sel)];
const row = () => q('.project-item').find(r => r.querySelector('.project-name')?.textContent === 'notes')!;
const openMenu = () => click(row().querySelector<HTMLButtonElement>('.project-item-menu-btn')!);
const tiers = () => q<HTMLButtonElement>('.project-item-menu [role=menuitemradio]');

async function show(summary: ProjectSummary) {
  hub = new FakeHub([summary], [work]);
  await connectServers({ A: hub });
  view = await render(<Shell />);
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  sessionStorage.clear();
  Object.assign(host, { sound: true, sets: [], answers: true });
});

afterEach(() => {
  view?.unmount();
  view = undefined;
});

describe('a row', () => {
  it('offers the three tiers in its menu, the current one checked, beside Delete', async () => {
    await show(session());

    await openMenu();

    expect(tiers().map(b => [b.textContent, b.getAttribute('aria-checked')])).toEqual([
      ['Quiet', 'false'], ['✓ Normal', 'true'], ['Important', 'false'],
    ]);
    expect(q('.project-item-menu [role=menuitem]').map(b => b.textContent)).toEqual(['Delete…']);
  });

  it('sets the tier on the server, and stars the row when the server pushes it back', async () => {
    await show(session());
    expect(row().querySelector('.importance-mark')).toBeNull();

    await openMenu();
    await click(tiers().find(b => b.textContent === 'Important')!);

    expect(hub.importances).toEqual([{ projectId: ID, importance: 'Important' }]);
    await vi.waitFor(() => expect(row().querySelector('.importance-mark.important')?.textContent).toBe('★'));
    expect(document.querySelector('.project-item-menu')).toBeNull();
  });

  it('tags a quiet session, and sends nothing for the tier it has', async () => {
    await show(session({ Importance: 'Quiet' }));
    expect(row().querySelector('.importance-mark.quiet')?.textContent).toBe('quiet');

    await openMenu();
    await click(tiers().find(b => b.textContent === '✓ Quiet')!);

    expect(hub.importances).toEqual([]);
  });
});

describe('the project header', () => {
  it('shows the tier pressed, and sets another', async () => {
    hub = new FakeHub([session({ Importance: 'Important' })], [work]);
    await connectServers({ A: hub });
    view = await render(<ImportancePicker serverId="A" projectId={ID} importance="Important" />);

    const choices = q<HTMLButtonElement>('.importance-choice');
    expect(choices.map(b => [b.textContent, b.getAttribute('aria-pressed')])).toEqual([
      ['Quiet', 'false'], ['Normal', 'false'], ['★', 'true'],
    ]);

    await click(choices[0]);
    await click(choices[2]);

    expect(hub.importances).toEqual([{ projectId: ID, importance: 'Quiet' }]);
  });
});

describe('the inbox', () => {
  const item = (extra: Partial<AttentionItem>): AttentionItem => ({
    ProjectId: ID, ProjectName: 'notes', Profile: 'Default', Root: 'work', Kind: 'Finished', Since: '2026-10-03T10:00:00Z', Text: 'Done.', ...extra,
  });

  it('stars an important session\'s item, and no other', async () => {
    hub = new FakeHub([], []);
    await connectServers({ A: hub });
    await act(async () => hub.callbacks.onAttentionChanged?.([item({ Importance: 'Important', Alert: 'Interrupt' })]));
    view = await render(<Inbox variant="screen" />);
    expect(view.container.querySelector('.inbox-item .importance-mark.important')).not.toBeNull();

    await act(async () => hub.callbacks.onAttentionChanged?.([item({ Importance: 'Quiet', Alert: 'Inbox' })]));
    // A quiet session's item is in the inbox still: quiet decides how loud, never whether it is there
    expect(view.container.querySelectorAll('.inbox-item')).toHaveLength(1);
    expect(view.container.querySelector('.inbox-item .importance-mark')).toBeNull();
  });
});

describe('the settings', () => {
  const soundToggle = () => q('.settings-item').find(i => i.textContent?.includes('Sound for important sessions'));

  it('show this device\'s sound switch as the app keeps it, and turn it off there', async () => {
    view = await render(<AppSettings />);
    await vi.waitFor(() => expect(soundToggle()).toBeDefined());
    const toggle = soundToggle()!.querySelector<HTMLElement>('input, button, [role=switch], .toggle')!;

    await click(toggle);

    await vi.waitFor(() => expect(host.sets).toEqual([false]));
  });

  it('leave the switch out when the app does not say', async () => {
    host.answers = false;
    view = await render(<AppSettings />);
    await act(async () => {});

    expect(soundToggle()).toBeUndefined();
  });
});
