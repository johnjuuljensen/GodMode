// @vitest-environment jsdom
/**
 * A plain click in the output above the composer focuses the composer (#435), and nothing else does:
 * a click that ends a selection or a drag, one on a link or a tool call's row, one while a question's
 * options take the keys, a tap with a coarse pointer. Renders ProjectView on the real store, with
 * Virtuoso replaced by a plain scroller that takes the focus as Virtuoso's does (tabIndex 0).
 */
import { act, type ReactNode } from 'react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { FakeHub, project, root, connectServers } from '../../test/fakeHub';
import { render, type Rendered } from '../../test/render';
import { parseClaudeMessage, type TranscriptItem } from '../../signalr/parseMessage';
import type { OutputMessage } from '../../signalr/hub';
import type { PendingQuestion } from '../../signalr/types';
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
    <div className={className} tabIndex={0} data-virtuoso-scroller>{data.map((item, i) => <div key={item.key}>{itemContent(i, item)}</div>)}</div>,
}));

// A mouse, unless a test says the pointer is coarse
let finePointer = true;
const otherMedia = window.matchMedia;
window.matchMedia = (query: string) => query !== '(pointer: fine)' ? otherMedia(query) : ({ ...otherMedia(query), matches: finePointer });

const assistant = (offset: number, content: unknown[]): OutputMessage => ({
  offset,
  message: parseClaudeMessage(JSON.stringify({ type: 'assistant', message: { role: 'assistant', content } })),
});
const output = [
  assistant(10, [{ type: 'text', text: 'Done: see [the docs](https://example.com/docs) for more.' }]),
  assistant(20, [{ type: 'tool_use', id: 't1', name: 'Bash', input: { command: 'npm test' } }]),
];

const question: PendingQuestion = {
  RequestId: 'r1',
  RequestedAt: '2026-10-03T12:00:00Z',
  Questions: [{ Question: 'Which way?', Options: [{ Label: 'Left' }, { Label: 'Right' }], MultiSelect: false }],
};

const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;

const open = async (pending?: PendingQuestion) => {
  hub = new FakeHub([{ ...project('p1', 'busy', pending ? 'WaitingInput' : 'Running', '2026-10-03T12:00:00Z'), PendingQuestion: pending ?? null }], [root]);
  await connectServers({ A: hub });
  useAppStore.getState().selectProject('A', 'p1');
  view = await render(<ProjectView serverId="A" projectId="p1" />);
  await act(async () => {
    hub.lastReplay('p1').batch(0, output);
    hub.lastReplay('p1').complete(20);
  });
};

const input = () => view.container.querySelector<HTMLTextAreaElement>('textarea.project-input')!;
const composerFocused = () => document.activeElement === input();
const el = (selector: string) => view.container.querySelector<HTMLElement>(selector)!;

/**
 * A mouse press at `from` and its release at `to`, as a browser sends them: focus goes to the press's focusable
 * ancestor, and what the drag selects (`selecting`) is selected between the two.
 */
const mouseClick = (target: HTMLElement, from = { x: 10, y: 10 }, to = from, selecting?: () => void) => act(async () => {
  const at = (p: { x: number; y: number }) => ({ bubbles: true, cancelable: true, button: 0, clientX: p.x, clientY: p.y });
  if (target.dispatchEvent(new MouseEvent('mousedown', at(from)))) target.closest<HTMLElement>('button, a[href], [tabindex]')?.focus();
  selecting?.();
  target.dispatchEvent(new MouseEvent('mouseup', at(to)));
  target.dispatchEvent(new MouseEvent('click', at(to)));
});

beforeEach(() => {
  useAppStore.setState(initialState, true);
  finePointer = true;
  window.getSelection()?.removeAllRanges();
});

afterEach(() => view.unmount());

it('a plain click in the output focuses the composer, keeping its caret', async () => {
  await open();
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(input(), 'half a thought');
    input().setSelectionRange(4, 4);
  });
  await mouseClick(el('.ti-assistant p'));
  expect(composerFocused()).toBe(true);
  expect(input().selectionStart).toBe(4);
});

it('a click between rows, on the list itself, focuses the composer', async () => {
  await open();
  await mouseClick(el('.transcript-list'));
  expect(composerFocused()).toBe(true);
});

// Double and triple clicks select a word and a line with no drag: those too
it('a click that ends a selection leaves the focus, so the selection can be copied', async () => {
  await open();
  const text = el('.ti-assistant p');
  await mouseClick(text, undefined, undefined, () => {
    const range = document.createRange();
    range.selectNodeContents(text);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);
  });
  expect(composerFocused()).toBe(false);
  expect(window.getSelection()!.toString()).toContain('Done: see');
});

it('a drag that ends in the output is no click for the composer', async () => {
  await open();
  await mouseClick(el('.ti-assistant p'), { x: 10, y: 10 }, { x: 80, y: 30 });
  expect(composerFocused()).toBe(false);
});

it("a click on a link is the link's", async () => {
  await open();
  const link = el('.ti-assistant a');
  link.addEventListener('click', e => e.preventDefault());
  await mouseClick(link);
  expect(composerFocused()).toBe(false);
});

it("a click on a tool call's row opens it, and leaves the composer alone", async () => {
  await open();
  await mouseClick(el('.ti-tool .ti-fold'));
  expect(el('.ti-tool .ti-fold').getAttribute('aria-expanded')).toBe('true');
  expect(composerFocused()).toBe(false);
});

it("while a question's options take the keys, a click in the output leaves them there", async () => {
  await open(question);
  expect(el('.question-option-active').textContent).toContain('Left');
  await mouseClick(el('.ti-assistant p'));
  expect(composerFocused()).toBe(false);
});

it('a tap with a coarse pointer raises no keyboard', async () => {
  finePointer = false;
  await open();
  await mouseClick(el('.ti-assistant p'));
  expect(composerFocused()).toBe(false);
});
