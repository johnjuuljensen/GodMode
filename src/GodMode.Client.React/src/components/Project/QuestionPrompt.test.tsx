// @vitest-environment jsdom
/**
 * The question shortcuts (Enter, 1-9, arrows, Escape) against the controls beside them: typing in a
 * text field is typing, and one key press sends one answer (#170). A key on a dialog's or the inbox's
 * button is that button's (#240). A fold clicked keeps no focus, so the keys stay the prompt's (#218).
 * Renders ProjectView beside the inbox pane and the confirm dialog, as the Shell does, on the real store.
 */
import { act, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AttentionItem, PendingQuestion } from '../../signalr/types';
import { parseClaudeMessage } from '../../signalr/parseMessage';
import { FakeHub, project, root, connectServers } from '../../test/fakeHub';
import { render, typeInto, keyDown, pressKey, click, pointerClick, type Rendered } from '../../test/render';
import { useAppStore } from '../../store';
import { getOpenConfirm } from '../../confirmDialog';
import { ConfirmDialog } from '../ConfirmDialog';
import { Inbox } from '../Inbox/Inbox';
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
  useAppStore.getState().selectProject('A', 'p1');
  view = await render(<><Inbox variant="pane" /><ProjectView serverId="A" projectId="p1" /><ConfirmDialog /></>);
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

describe('beside a dialog and the inbox (#240)', () => {
  const button = (label: string, within: ParentNode = view.container) =>
    [...within.querySelectorAll('button')].find(b => b.textContent === label)!;
  /** The question was dismissed and the dismissal stored: what Escape on the prompt does. */
  const dismissed = () => Object.keys(useAppStore.getState().dismissedProjects);

  /** Taps the status pill's Stop: its dialog opens with Cancel focused. */
  async function openStopDialog() {
    await click(view.container.querySelector<HTMLElement>('.project-status-btn')!);
    expect(getOpenConfirm()?.request.title).toBe('Stop "asking"?');
    expect(document.activeElement?.textContent).toBe('Cancel');
  }

  it("Enter on the confirm dialog's Cancel closes the dialog and answers nothing", async () => {
    await openStopDialog();
    const event = await pressKey(document.activeElement as HTMLButtonElement, 'Enter');
    expect(event.defaultPrevented).toBe(false);
    expect(getOpenConfirm()).toBeNull();
    expect(hub.answers).toEqual([]);
  });

  it('Escape closes the dialog and leaves the question, with focus off its buttons', async () => {
    await openStopDialog();
    // A tap on the dialog's text takes focus off Cancel, to the page
    await act(async () => (document.activeElement as HTMLElement).blur());
    await keyDown(null, 'Escape');
    expect(getOpenConfirm()).toBeNull();
    expect(dismissed()).toEqual([]);
    expect(hub.answers).toEqual([]);
  });

  describe("on the inbox's buttons", () => {
    const item = (projectId: string, kind: AttentionItem['Kind'], extra: Partial<AttentionItem> = {}): AttentionItem => ({
      ProjectId: projectId, ProjectName: projectId, Kind: kind, Since: '2026-09-24T11:00:00Z', Text: `${projectId} ${kind}`, ...extra,
    });
    const itemEl = (projectId: string) => [...view.container.querySelectorAll<HTMLElement>('.inbox-item')]
      .find(el => el.querySelector('.inbox-item-name')?.textContent === projectId)!;

    beforeEach(async () => {
      await act(async () => hub.callbacks.onAttentionChanged?.([
        item('p2', 'Permission', {
          Permission: { RequestId: 'r2', ToolName: 'Bash', Input: {}, Summary: 'Bash: git push', RequestedAt: '2026-09-24T11:00:00Z' },
        }),
        item('p3', 'Finished'),
        item('p4', 'Error'),
      ]));
    });

    it('digits and Escape answer and dismiss nothing', async () => {
      for (const b of [button('Allow', itemEl('p2')), button('Mark seen', itemEl('p3'))]) {
        for (const key of ['1', '2', 'Escape']) {
          const event = await pressKey(b, key);
          expect(event.defaultPrevented, `${key} on ${b.textContent}`).toBe(false);
        }
      }
      expect(hub.answers).toEqual([]);
      expect(dismissed()).toEqual([]);
    });

    it("Enter is the button's own click, and no answer", async () => {
      await pressKey(button('Allow', itemEl('p2')), 'Enter');
      await pressKey(button('Mark seen', itemEl('p3')), 'Enter');
      await typeInto(itemEl('p4').querySelector('textarea')!, 'Try again');
      await pressKey(button('Send', itemEl('p4')), 'Enter');

      expect(hub.decisions).toEqual([{ projectId: 'p2', requestId: 'r2', decision: { Allow: true, Message: null } }]);
      expect(hub.seen).toEqual(['p3']);
      expect(hub.replies).toEqual([{ projectId: 'p4', text: 'Try again' }]);
      expect(hub.answers).toEqual([]);
    });
  });
});

describe('after a click (#218)', () => {
  const bashCall = {
    offset: 10,
    message: parseClaudeMessage(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Bash', input: { command: 'ls' } }] } })),
  };
  const right = [{ projectId: 'p1', requestId: 'r1', answers: { 'Which way?': 'Right' } }];
  let fold: HTMLButtonElement;

  beforeEach(async () => {
    await act(async () => {
      hub.lastReplay('p1').batch(0, [bashCall]);
      hub.lastReplay('p1').complete(10);
    });
    fold = view.container.querySelector<HTMLButtonElement>('.project-view .ti-fold')!;
  });

  it('a fold clicked with the question open leaves it its keys: a digit answers', async () => {
    await pointerClick(fold);
    expect(fold.getAttribute('aria-expanded')).toBe('true');
    await keyDown(document.activeElement, '2');
    expect(hub.answers).toEqual(right);
  });

  it("and so does the inbox pane's toggle", async () => {
    const toggle = view.container.querySelector<HTMLButtonElement>('.inbox-toggle')!;
    await pointerClick(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    await keyDown(document.activeElement, '2');
    expect(hub.answers).toEqual(right);
  });

  it('a fold reached by the keyboard is as before: Enter there opens it, and answers nothing', async () => {
    await pressKey(fold, 'Enter');
    expect(fold.getAttribute('aria-expanded')).toBe('true');
    expect(document.activeElement).toBe(fold);
    expect(hub.answers).toEqual([]);
  });
});
