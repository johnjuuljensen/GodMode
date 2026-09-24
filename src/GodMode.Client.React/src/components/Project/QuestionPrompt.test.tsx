// @vitest-environment jsdom
/**
 * The question shortcuts (Enter, 1-9, arrows) against the chat input beside them (#170): typing in a
 * text field is typing, and one key press sends one answer. Renders ProjectView on the real store.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PendingQuestion } from '../../signalr/types';
import { FakeHub, project, root, connectServers } from '../../test/fakeHub';
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

const pending: PendingQuestion = {
  RequestId: 'r1',
  RequestedAt: '2026-09-24T12:00:00Z',
  Questions: [{ Question: 'Which way?', Options: [{ Label: 'Left' }, { Label: 'Right' }], MultiSelect: false }],
};

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;
let input: HTMLTextAreaElement;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([{ ...project('p1', 'asking', 'WaitingInput', '2026-09-24T12:00:00Z'), PendingQuestion: pending }], [root]);
  await connectServers({ A: hub });
  view = await render(<ProjectView serverId="A" projectId="p1" />);
  input = view.container.querySelector('textarea.project-input')!;
  // The first option is highlighted
  expect(view.container.querySelector('.question-option-active')?.textContent).toContain('Left');
});

afterEach(() => view.unmount());

describe('in the chat input', () => {
  it('Enter sends the typed text once, and not the highlighted option', async () => {
    input.focus();
    await typeInto(input, 'Neither, go back');
    await keyDown(input, 'Enter');
    expect(hub.replies).toEqual([{ projectId: 'p1', text: 'Neither, go back' }]);
    expect(hub.answers).toEqual([]);
  });

  it('a digit is typed, not sent', async () => {
    input.focus();
    const event = await keyDown(input, '2');
    expect(event.defaultPrevented).toBe(false);
    expect(hub.answers).toEqual([]);
    expect(hub.replies).toEqual([]);
  });

  it('the arrow keys move the caret, not the highlight', async () => {
    input.focus();
    const event = await keyDown(input, 'ArrowDown');
    expect(event.defaultPrevented).toBe(false);
    expect(view.container.querySelector('.question-option-active')?.textContent).toContain('Left');
  });
});

describe('outside a text field', () => {
  it('Enter answers with the highlighted option, once', async () => {
    await keyDown(null, 'ArrowDown');
    const event = await keyDown(null, 'Enter');
    expect(event.defaultPrevented).toBe(true);
    expect(hub.answers).toEqual([{ projectId: 'p1', requestId: 'r1', answers: { 'Which way?': 'Right' } }]);
    expect(hub.replies).toEqual([]);
  });

  it('a digit answers with that option', async () => {
    await keyDown(null, '2');
    expect(hub.answers).toEqual([{ projectId: 'p1', requestId: 'r1', answers: { 'Which way?': 'Right' } }]);
  });
});
