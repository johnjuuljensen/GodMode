// @vitest-environment jsdom
/**
 * A message sent from a project's view shows at once, pending, until claude echoes it (#383): the
 * echo takes its place, matched by text and in order; a send that fails takes it away; one the
 * session stops or fails with stays, marked not taken. Renders ProjectView on the real store, with
 * Virtuoso replaced by a plain list, since jsdom lays nothing out.
 */
import { act, type ReactNode } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { FakeHub, project, root, status, connectServers } from '../../test/fakeHub';
import { render, typeInto, keyDown, type Rendered } from '../../test/render';
import { parseClaudeMessage, type TranscriptItem } from '../../signalr/parseMessage';
import type { OutputMessage } from '../../signalr/hub';
import { useAppStore } from '../../store';
import { ProjectView } from './ProjectView';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));
vi.mock('react-virtuoso', () => ({
  Virtuoso: ({ data, itemContent, className }: { data: TranscriptItem[]; itemContent: (i: number, item: TranscriptItem) => ReactNode; className?: string }) =>
    <div className={className}>{data.map((item, i) => <div key={item.key}>{itemContent(i, item)}</div>)}</div>,
}));

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;

/** claude's echo of a message the user sent, as --replay-user-messages writes it. */
const echo = (offset: number, text: string): OutputMessage => ({
  offset,
  message: parseClaudeMessage(JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'text', text }] }, isReplay: true })),
});
const result = (offset: number): OutputMessage => ({
  offset,
  message: parseClaudeMessage(JSON.stringify({ type: 'result', subtype: 'success', is_error: false, result: '', num_turns: 0 })),
});
const push = (line: OutputMessage) => act(async () => hub.callbacks.onOutputReceived?.('p1', line));

/** The user's bubbles, top to bottom: their text, and `pending` or `notTaken` when not echoed. */
const bubbles = () => [...view.container.querySelectorAll('.ti-user')].map(el => {
  const text = el.querySelector('.ti-user-bubble')!.textContent;
  return el.classList.contains('ti-user-waiting') ? `${text} (pending)` : el.classList.contains('ti-user-notTaken') ? `${text} (not taken)` : text;
});

const send = async (text: string) => {
  const input = view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
  await typeInto(input, text);
  await keyDown(input, 'Enter');
};

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([project('p1', 'busy', 'Running', '2026-10-03T12:00:00Z')], [root]);
  await connectServers({ A: hub });
  useAppStore.getState().selectProject('A', 'p1');
  view = await render(<ProjectView serverId="A" projectId="p1" />);
  await act(async () => hub.lastReplay('p1').answer(0, [10]));
});

afterEach(() => view.unmount());

it('shows a sent message at once as pending, and its echo takes its place', async () => {
  await send('Also update the README');
  expect(hub.replies).toEqual([{ projectId: 'p1', text: 'Also update the README' }]);
  expect(bubbles()).toEqual(['Also update the README (pending)']);
  expect(view.container.querySelector('.ti-user-pending')?.textContent).toBe('Waiting for claude');

  await push(echo(20, 'Also update the README'));
  expect(bubbles()).toEqual(['Also update the README']);
  expect(useAppStore.getState().pendingSends).toEqual({});
});

it('the first message of a session that has had no turn shows pending too', async () => {
  const freshHub = new FakeHub([project('p2', 'fresh', 'Idle', '2026-10-03T12:00:00Z')], [root]);
  await connectServers({ B: freshHub });
  useAppStore.getState().selectProject('B', 'p2');
  const fresh = await render(<ProjectView serverId="B" projectId="p2" />);
  try {
    await act(async () => freshHub.lastReplay('p2').answer(0, []));
    const input = fresh.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
    await typeInto(input, 'Draft the mail');
    await keyDown(input, 'Enter');
    expect(fresh.container.querySelector('.ti-user-waiting .ti-user-bubble')?.textContent).toBe('Draft the mail');
  } finally {
    fresh.unmount();
  }
});

it('the same text sent twice is matched in order, and an echo out of order takes only its own', async () => {
  await send('yes');
  await send('Then run the tests');
  await send('yes');
  expect(bubbles()).toEqual(['yes (pending)', 'Then run the tests (pending)', 'yes (pending)']);

  await push(echo(20, 'Then run the tests'));
  expect(bubbles()).toEqual(['Then run the tests', 'yes (pending)', 'yes (pending)']);
  await push(echo(30, 'yes'));
  expect(bubbles()).toEqual(['Then run the tests', 'yes', 'yes (pending)']);
  await push(echo(40, 'yes'));
  expect(bubbles()).toEqual(['Then run the tests', 'yes', 'yes']);
});

it('an echo of the same text from before the send does not take it', async () => {
  await send('continue');
  // The transcript is replayed from the start, as after a reconnect to a server that started the file over
  await act(async () => {
    hub.generations.p1 = 'g1';
    hub.callbacks.onOutputBatch?.('p1', hub.lastReplay('p1').subscriptionId, 'g1', 0, [echo(5, 'continue')]);
  });
  expect(bubbles()).toEqual(['continue', 'continue (pending)']);
  await push(echo(20, 'continue'));
  expect(bubbles()).toEqual(['continue', 'continue']);
});

it('a send that fails takes the pending message away, says why, and gives the text back', async () => {
  hub.refuseReply = '/model is not sent: GodMode sets a session\'s model and effort.';
  await send('/model opus');
  expect(bubbles()).toEqual([]);
  expect(view.container.querySelector('[role="alert"]')?.textContent).toBe(hub.refuseReply);
  expect(view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!.value).toBe('/model opus');
  expect(useAppStore.getState().pendingSends).toEqual({});
});

it('a message pending when the session stops stays, marked not taken, until its echo comes', async () => {
  await send('Use the other API');
  await act(async () => hub.callbacks.onStatusChanged?.('p1', status('p1', 'Stopped')));
  expect(bubbles()).toEqual(['Use the other API (not taken)']);
  expect(view.container.querySelector('.ti-user-pending')?.textContent).toBe('Not taken: the session stopped');

  // A status that does not change the state marks nothing
  await send('And then?');
  await act(async () => hub.callbacks.onStatusChanged?.('p1', status('p1', 'Stopped')));
  expect(bubbles()).toEqual(['Use the other API (not taken)', 'And then? (pending)']);

  await act(async () => hub.callbacks.onStatusChanged?.('p1', status('p1', 'Running')));
  await act(async () => hub.callbacks.onStatusChanged?.('p1', status('p1', 'Error')));
  expect(bubbles()).toEqual(['Use the other API (not taken)', 'And then? (not taken)']);
  await push(echo(20, 'Use the other API'));
  expect(bubbles()).toEqual(['Use the other API', 'And then? (not taken)']);
});

// The server takes it as the answer or the denial, and writes nothing to claude: no echo comes (answersPending)
it.each([
  ['a permission prompt', { PendingPermission: { RequestId: 'r1', ToolName: 'Bash', Summary: 'rm -rf build' } }],
  ['a question', { PendingQuestion: { RequestId: 'r2', Questions: [], RequestedAt: '2026-10-03T12:00:00Z' } }],
])('a message sent with %s open answers it, and is not left pending', async (_, pending) => {
  await act(async () => useAppStore.setState(state => ({
    serverConnections: state.serverConnections.map(c => ({ ...c, projects: c.projects.map(p => ({ ...p, ...pending } as typeof p)) })),
  })));
  await useAppStore.getState().sendReply('A', 'p1', 'No, use the staging database');
  expect(hub.replies).toEqual([{ projectId: 'p1', text: 'No, use the staging database' }]);
  expect(useAppStore.getState().pendingSends).toEqual({});
});

it('a slash command, which claude does not echo as text, is taken by the end of its turn', async () => {
  await send('/compact');
  expect(bubbles()).toEqual(['/compact (pending)']);
  await push(result(20));
  expect(bubbles()).toEqual([]);
});

it('a /clear is taken when the output starts over', async () => {
  await send('/clear');
  await act(async () => hub.restartOutput('p1', 'g2'));
  expect(bubbles()).toEqual([]);
});
