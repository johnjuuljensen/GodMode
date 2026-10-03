// @vitest-environment jsdom
/**
 * An Error project is answered where the inbox's "open the project" lands (#240): its input takes a
 * reply, through ReplyAndResume, as the inbox's Error item does. A project the server does not have
 * says so (#239). A plain-text question is no prompt: its text is on screen already, and the composer answers
 * it (#447). Renders ProjectView on the real store.
 */
import { act, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PendingQuestion, ProjectStatus } from '../../signalr/types';
import { parseClaudeMessage } from '../../signalr/parseMessage';
import { FakeHub, project, root, connectServers, status } from '../../test/fakeHub';
import { render, typeInto, keyDown, type Rendered } from '../../test/render';
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
// jsdom lays nothing out, so Virtuoso would render no rows: this one renders them all
vi.mock('react-virtuoso', () => ({
  Virtuoso: ({ data, itemContent, className }: { data: unknown[]; itemContent: (i: number, item: unknown) => ReactNode; className?: string }) => (
    <div className={className}>{data.map((row, i) => <div key={i}>{itemContent(i, row)}</div>)}</div>
  ),
}));

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([project('p1', 'broken', 'Error', '2026-09-24T12:00:00Z')], [root]);
  await connectServers({ A: hub });
  useAppStore.getState().selectProject('A', 'p1');
  view = await render(<ProjectView serverId="A" projectId="p1" />);
});

afterEach(() => view.unmount());

// A deep link, a reload or a forward to a project the server does not have once showed "Loading..." for good, with live controls (#239)
it('a project its server does not have says so, and offers nothing to act on', async () => {
  const gone = await render(<ProjectView serverId="A" projectId="p9" />);
  try {
    const el = gone.container;
    expect(el.querySelector('.project-messages-empty')?.textContent).toBe('Project not found');
    expect(el.querySelector<HTMLTextAreaElement>('textarea.project-input')!.disabled).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('.delete-btn')!.disabled).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('.project-status-btn')!.disabled).toBe(true);

    // Not before the server has listed its projects on this connection: it may be among them
    await act(async () => useAppStore.setState({ projectsListed: {} }));
    expect(el.querySelector('.project-messages-empty')?.textContent).toBe('Loading...');
  } finally {
    gone.unmount();
  }
});

it("an Error project's input is enabled and sends through ReplyAndResume", async () => {
  const input = view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
  expect(input.disabled).toBe(false);

  await typeInto(input, 'The migration failed; retry it');
  await keyDown(input, 'Enter');

  expect(hub.replies).toEqual([{ projectId: 'p1', text: 'The migration failed; retry it' }]);
  expect(input.value).toBe('');
});

// Created with no prompt (#352): claude waits for the first message, and Resume has nothing to do
it('a session that has had no turn asks for its first message, and offers no Resume', async () => {
  const freshHub = new FakeHub([project('p2', 'outbound', 'Idle', '2026-09-24T12:00:00Z')], [root]);
  await connectServers({ B: freshHub });
  useAppStore.getState().selectProject('B', 'p2');
  const fresh = await render(<ProjectView serverId="B" projectId="p2" />);
  try {
    const el = fresh.container;
    // Until its output is replayed, an empty transcript may be one not loaded yet
    expect(el.querySelector('.project-messages-empty')?.textContent).toBe('Loading...');
    await act(async () => freshHub.lastReplay('p2').answer(0, []));
    expect(el.querySelector('.project-messages-empty')?.textContent).toBe('Send the first message to start.');
    expect(el.querySelector<HTMLTextAreaElement>('textarea.project-input')!.placeholder).toBe('Type the first message...');
    expect(el.querySelector('.project-status-action')).toBeNull();
    expect(el.querySelector<HTMLButtonElement>('.project-status-btn')!.disabled).toBe(true);

    const input = el.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
    await typeInto(input, 'Draft the mail to the supplier');
    await keyDown(input, 'Enter');
    expect(freshHub.replies).toEqual([{ projectId: 'p2', text: 'Draft the mail to the supplier' }]);
  } finally {
    fresh.unmount();
  }
});

// Slash commands (#31): the composer offers the session's, and a command the server refuses says why
it('offers the slash commands a /word starts, and Enter completes the one picked', async () => {
  const commandsHub = new FakeHub([{ ...project('p3', 'cmds', 'Idle', '2026-10-02T12:00:00Z'), SlashCommands: ['clear', 'compact', 'context', 'loop'] }], [root]);
  await connectServers({ C: commandsHub });
  useAppStore.getState().selectProject('C', 'p3');
  const cmds = await render(<ProjectView serverId="C" projectId="p3" />);
  try {
    const el = cmds.container;
    const input = el.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
    const offered = () => [...el.querySelectorAll('.reply-commands [role="option"]')].map(o => o.textContent);

    await typeInto(input, '/c');
    expect(offered()).toEqual(['/clear', '/compact', '/context']);
    await keyDown(input, 'ArrowDown');
    await keyDown(input, 'Enter');
    expect(input.value).toBe('/compact ');
    expect(offered()).toEqual([]);
    expect(commandsHub.replies).toEqual([]);

    // A command typed whole is sent by Enter, as any text is
    await typeInto(input, '/clear');
    expect(offered()).toEqual([]);
    await keyDown(input, 'Enter');
    expect(commandsHub.replies).toEqual([{ projectId: 'p3', text: '/clear' }]);
  } finally {
    cmds.unmount();
  }
});

it('a message the server refuses says why, and comes back to the input', async () => {
  hub.refuseReply = '/model is not sent: GodMode sets a session\'s model and effort.';
  const input = view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;

  await typeInto(input, '/model opus');
  await keyDown(input, 'Enter');

  expect(view.container.querySelector('[role="alert"]')?.textContent).toBe(hub.refuseReply);
  expect(input.value).toBe('/model opus');
});

// The box repeated the turn's last line, right below it, and an offer ("Shall I file it?") is no blocking question (#447)
describe('a question in plain text (#447)', () => {
  const offer = 'Builds clean. Shall I file it?';
  const asks: ProjectStatus = { ...status('p4', 'WaitingInput'), Name: 'asker', CurrentQuestion: offer };
  const pending: PendingQuestion = {
    RequestId: 'r1',
    RequestedAt: '2026-10-03T12:00:00Z',
    Questions: [{ Question: 'Which way?', Options: [{ Label: 'Left' }, { Label: 'Right' }], MultiSelect: false }],
  };

  async function viewAsking(extra: Partial<ProjectStatus> = {}) {
    const askHub = new FakeHub([{ ...project('p4', 'asker', 'WaitingInput', '2026-10-03T12:00:00Z'), CurrentQuestion: offer, ...extra }], [root]);
    await connectServers({ D: askHub });
    useAppStore.getState().selectProject('D', 'p4');
    const asking = await render(<ProjectView serverId="D" projectId="p4" />);
    const turn = parseClaudeMessage(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: offer }] } }));
    await act(async () => {
      askHub.lastReplay('p4').batch(0, [{ offset: 10, message: turn }]);
      askHub.lastReplay('p4').complete(10);
      askHub.callbacks.onStatusChanged?.('p4', { ...asks, ...extra });
    });
    return asking;
  }

  it('shows no prompt for a WaitingInput status with a CurrentQuestion and no AskUserQuestion: the text is in the transcript', async () => {
    const asking = await viewAsking();
    try {
      expect(asking.container.querySelector('.question-prompt')).toBeNull();
      expect(asking.container.textContent).toContain(offer);
      expect(asking.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!.disabled).toBe(false);
    } finally {
      asking.unmount();
    }
  });

  it('still shows a pending AskUserQuestion with its options', async () => {
    const asking = await viewAsking({ PendingQuestion: pending });
    try {
      const prompt = asking.container.querySelector('.question-prompt')!;
      expect(prompt.querySelector('.question-text')?.textContent).toBe('Which way?');
      expect([...prompt.querySelectorAll('.question-option-label')].map(o => o.textContent)).toEqual(['Left', 'Right']);
    } finally {
      asking.unmount();
    }
  });
});
