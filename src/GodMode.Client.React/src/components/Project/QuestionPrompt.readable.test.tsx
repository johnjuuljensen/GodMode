// @vitest-environment jsdom
/**
 * A pending question reads in full in the project view (#454): every question of the request, its header,
 * its options and their descriptions, as wrapped text, with no rule that cuts it; one answer per question,
 * a multi-select's labels joined. Renders ProjectView on the real store.
 */
import { join } from 'node:path';
import { act, type ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FakeHub, project, root, status, connectServers } from '../../test/fakeHub';
import { render, click, keyDown, type Rendered } from '../../test/render';
import { classesIn, cuttingRules, longRequest, ruleOf, textsOf } from '../../test/readable';
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
  Virtuoso: ({ data, itemContent, className }: { data: unknown[]; itemContent: (i: number, item: unknown) => ReactNode; className?: string }) => (
    <div className={className}>{data.map((row, i) => <div key={i}>{itemContent(i, row)}</div>)}</div>
  ),
}));

const css = join(import.meta.dirname, 'QuestionPrompt.css');
const initialState = useAppStore.getState();
let hub: FakeHub;
let view: Rendered;
const prompt = () => view.container.querySelector<HTMLElement>('.question-prompt')!;
const option = (label: string) => [...prompt().querySelectorAll<HTMLButtonElement>('.question-option')]
  .find(b => b.querySelector('.question-option-label')?.textContent === label)!;

beforeEach(async () => {
  useAppStore.setState(initialState, true);
  hub = new FakeHub([{ ...project('p1', 'asking', 'WaitingInput', '2026-10-03T12:00:00Z'), PendingQuestion: longRequest }], [root]);
  await connectServers({ A: hub });
  useAppStore.getState().selectProject('A', 'p1');
  view = await render(<ProjectView serverId="A" projectId="p1" />);
});

afterEach(() => view.unmount());

describe('a request of three long questions', () => {
  it('shows every word of every question, header, option and description', () => {
    const shown = prompt().textContent!;
    for (const text of textsOf(longRequest)) expect(shown).toContain(text);
    expect(prompt().querySelectorAll('.question-item')).toHaveLength(3);
  });

  it('has no rule that cuts its text off', () => {
    expect(cuttingRules(css, classesIn(prompt()))).toEqual([]);
  });

  it('shows a description as text under its label, not in a tooltip', () => {
    const description = longRequest.Questions[0].Options[0].Description!;
    expect(option('Yes, repair now').querySelector('.question-option-desc')?.textContent).toBe(description);
    expect([...prompt().querySelectorAll('[title]')].map(el => el.getAttribute('title'))).not.toContain(description);
  });

  it('sets a description at a readable size and contrast', () => {
    const desc = ruleOf(css, '.question-option-desc');
    expect(Number(/font-size:\s*([\d.]+)px/.exec(desc)?.[1])).toBeGreaterThanOrEqual(12);
    expect(desc).toContain('var(--text-secondary)');
  });

  it('shows a multi-select as one, and answers one per question, in any order', async () => {
    const [repair, issues, fixes] = longRequest.Questions;
    expect(prompt().querySelectorAll('.question-multi')).toHaveLength(1);

    await click(option('Start them'));
    await click(option('Yes, repair now'));
    expect(hub.answers).toEqual([]);

    // A multi-select's options check and uncheck, and its Answer sends them in the order offered
    await click(option('The leak'));
    await click(option('The typo'));
    await click(option('The crash'));
    await click(option('The typo'));
    expect(option('The leak').getAttribute('aria-pressed')).toBe('true');
    expect(option('The typo').getAttribute('aria-pressed')).toBe('false');
    await click([...prompt().querySelectorAll<HTMLButtonElement>('.question-answer')][0]);

    expect(hub.answers).toEqual([{ projectId: 'p1', requestId: 'long', answers: {
      [repair.Question]: 'Yes, repair now',
      [issues.Question]: 'The crash, The leak',
      [fixes.Question]: 'Start them',
    } }]);
  });

  it('keys go to the first question not answered: a digit checks a multi-select, Enter answers it', async () => {
    await keyDown(null, '2');
    expect(hub.answers).toEqual([]);
    // The second question is open now: 3 checks The leak
    await keyDown(null, '3');
    await keyDown(null, 'Enter');
    await keyDown(null, '1');
    expect(hub.answers).toEqual([{ projectId: 'p1', requestId: 'long', answers: {
      [longRequest.Questions[0].Question]: 'No, leave it',
      [longRequest.Questions[1].Question]: 'The leak',
      [longRequest.Questions[2].Question]: 'Start them',
    } }]);
  });
});

describe("a multi-select's checks (#489)", () => {
  const checkedLabels = () => [...prompt().querySelectorAll('.question-option[aria-pressed="true"]')]
    .map(b => b.querySelector('.question-option-label')?.textContent);
  /** A StatusChanged for the project, its pending question copied into new objects, as the store takes every push. */
  const push = (request = longRequest) => act(async () => hub.callbacks.onStatusChanged?.('p1', {
    ...status('p1', 'WaitingInput'), Name: 'asking', UpdatedAt: '2026-10-03T12:00:00Z',
    PendingQuestion: structuredClone(request),
  }));

  beforeEach(async () => {
    await click(option('Yes, repair now'));
    await click(option('The crash'));
    await click(option('The leak'));
    expect(checkedLabels()).toEqual(['Yes, repair now', 'The crash', 'The leak']);
  });

  it('stay checked through a status push of the same request', async () => {
    await push();
    expect(checkedLabels()).toEqual(['Yes, repair now', 'The crash', 'The leak']);
  });

  it('start over for a new request', async () => {
    await push({ ...longRequest, RequestId: 'next' });
    expect(checkedLabels()).toEqual([]);
  });
});
