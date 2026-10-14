// @vitest-environment jsdom
/**
 * Opening a project settles its item, by kind (#592): with the window in front, the server is told the project was
 * opened (MarkOpened), and what it settles stays settled once the view moves on. A pending AskUserQuestion it
 * acknowledges leaves the inbox for good, and the left list still marks its project as waiting. An item it leaves (an
 * earlier escalation) is hidden while the project is open, and back after (#549). Renders the Shell on the real store.
 */
import { act } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, AttentionKind, ProjectSummary } from '../../signalr/types';
import { FakeHub, project, root, connectServers, flush } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { answer } from '../../test/appShell';
import { useAppStore, waitsOnUser } from '../../store';
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

const item = (projectId: string, kind: AttentionKind, since: string, more: Partial<AttentionItem> = {}): AttentionItem => ({
  ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: since, Text: `${projectId} ${kind}`, ...more,
});
const question = { RequestId: 'q1', Questions: [], RequestedAt: '2026-10-14T09:00:00Z' };

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;
let focused: boolean;

const listed = () => [...view.container.querySelectorAll('.inbox-item .inbox-item-name')].map(e => e.textContent);
const open = (projectId: string) => act(async () => useAppStore.getState().selectProject('A', projectId));
const leave = () => act(async () => useAppStore.getState().clearSelection());
const push = (attention: AttentionItem[]) => act(async () => { hub.callbacks.onAttentionChanged?.(attention); });
const waitingRows = () => [...view.container.querySelectorAll('.project-item.waiting .project-name')].map(e => e.textContent);

async function start(attention: AttentionItem[], projects: ProjectSummary[] = ['x', 'y'].map(id => project(id, id, 'Idle', '2026-10-14T09:00:00Z'))) {
  hub = new FakeHub(projects, [root]);
  await connectServers({ A: hub });
  await push(attention);
  view = await render(<Shell />);
  await flush();
}

beforeEach(() => {
  useAppStore.setState(initialState, true);
  history.replaceState(null, '', '#/');
  answer('session.open', true);
  focused = true;
  vi.spyOn(document, 'hasFocus').mockImplementation(() => focused);
});

afterEach(() => {
  view.unmount();
  vi.restoreAllMocks();
});

describe('opening a project', () => {
  it("marks its Finished item seen, and leaving it does not bring it back", async () => {
    await start([item('x', 'Finished', '2026-10-14T09:00:00Z'), item('y', 'Finished', '2026-10-14T09:01:00Z')]);

    await open('x');
    expect(hub.opened).toEqual(['x']);

    // The server has seen it, and lists it no more
    await push([item('y', 'Finished', '2026-10-14T09:01:00Z')]);
    await leave();
    expect(listed()).toEqual(['y']);
    expect(hub.opened).toEqual(['x']);
  });

  it('counts only with the window in front, and once it comes to the front', async () => {
    await start([item('x', 'Finished', '2026-10-14T09:00:00Z')]);
    focused = false;
    await act(async () => { window.dispatchEvent(new Event('blur')); });

    await open('x');
    expect(hub.opened).toEqual([]);

    focused = true;
    await act(async () => { window.dispatchEvent(new Event('focus')); });
    expect(hub.opened).toEqual(['x']);
  });

  it("acknowledges a pending AskUserQuestion: it does not come back in the inbox, and the project's row stays waiting", async () => {
    const asking = { ...project('x', 'x', 'WaitingInput', '2026-10-14T09:00:00Z'), PendingQuestion: question, CurrentQuestion: 'Which color?' };
    await start([item('x', 'Question', '2026-10-14T09:00:00Z', { Question: question })], [asking, project('y', 'y', 'Idle', '2026-10-14T09:00:00Z')]);

    await open('x');
    expect(hub.opened).toEqual(['x']);
    await push([item('x', 'Question', '2026-10-14T09:00:00Z', { Question: question, Acknowledged: true, Alert: 'Inbox' })]);
    await leave();

    expect(listed()).toEqual([]);
    expect(waitingRows()).toEqual(['x']);
    // Acknowledged, it has nothing more to settle
    await open('x');
    expect(hub.opened).toEqual(['x']);
  });

  it('hides an earlier escalation, which the server leaves, while open, and brings it back after', async () => {
    const escalation = item('x', 'Escalation', '2026-10-14T08:00:00Z');
    await start([escalation]);

    await open('x');
    expect(hub.opened).toEqual(['x']);
    expect(listed()).toEqual([]);
    // The server leaves it as it was: it is not asked about again while it is shown
    await push([{ ...escalation }]);
    expect(hub.opened).toEqual(['x']);

    await leave();
    expect(listed()).toEqual(['x']);
  });
});

it("stops counting a question in plain text as waiting once it is seen, a stopped session's too", () => {
  const stopped = { ...project('x', 'x', 'Stopped', '2026-10-14T09:00:00Z'), CurrentQuestion: 'I need a restart.' };
  expect(waitsOnUser(stopped)).toBe(true);
  expect(waitsOnUser({ ...stopped, QuestionSeen: true })).toBe(false);
  expect(waitsOnUser({ ...stopped, State: 'WaitingInput', QuestionSeen: true })).toBe(false);
  // An AskUserQuestion waits until it is answered, seen or not
  expect(waitsOnUser({ ...stopped, State: 'WaitingInput', QuestionSeen: true, PendingQuestion: question })).toBe(true);
});
