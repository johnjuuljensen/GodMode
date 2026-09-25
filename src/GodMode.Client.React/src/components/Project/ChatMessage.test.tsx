// @vitest-environment jsdom
/**
 * Transcript rows: markdown that renders inert whatever claude (or a tool result it quotes) wrote,
 * and tool calls that are one line until opened.
 */
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { render, type Rendered } from '../../test/render';
import { ChatMessage } from './ChatMessage';
import { buildTranscript, parseClaudeMessage, type TranscriptItem } from '../../signalr/parseMessage';

let view: Rendered | undefined;
afterEach(() => { view?.unmount(); view = undefined; });

const assistant = (text: string): TranscriptItem => ({ kind: 'assistantText', key: 'a', text });
const line = (json: object) => parseClaudeMessage(JSON.stringify(json));

async function show(item: TranscriptItem, expanded = false, onToggle = () => {}) {
  view = await render(<ChatMessage item={item} expanded={expanded} onToggle={onToggle} />);
  return view.container;
}

describe('markdown in a reply', () => {
  it('renders headings, emphasis, lists, tables and code', async () => {
    const el = await show(assistant('## Done\n\n**bold** and `code`\n\n- one\n- two\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```js\nx()\n```'));
    expect(el.querySelector('h2')?.textContent).toBe('Done');
    expect(el.querySelector('strong')?.textContent).toBe('bold');
    expect(el.querySelectorAll('li')).toHaveLength(2);
    expect(el.querySelector('td')?.textContent).toBe('1');
    expect(el.querySelector('pre code')?.textContent).toBe('x()\n');
  });

  it('renders a script, an onerror handler and a javascript: link inert', async () => {
    const alert = vi.fn();
    (window as unknown as { alert: typeof alert }).alert = alert;
    const el = await show(assistant(
      'Hi <script>alert(1)</script>\n\n<img src="data:image/png;base64,iVBORw0KGgo=" onerror="alert(2)">\n\n[click](javascript:alert(3)) <a href="javascript:alert(4)">raw</a>\n\n<iframe src="https://example.invalid"></iframe>',
    ));
    expect(el.querySelector('script')).toBeNull();
    expect(el.querySelector('iframe')).toBeNull();
    const img = el.querySelector('img')!;
    expect(img.getAttribute('onerror')).toBeNull();
    for (const a of el.querySelectorAll('a')) {
      expect(a.getAttribute('href') ?? '').not.toMatch(/javascript:/i);
    }
    expect(el.innerHTML).not.toMatch(/onerror|javascript:|<script/i);
    img.dispatchEvent(new Event('error'));
    expect(alert).not.toHaveBeenCalled();
  });

  it('opens a link outside the app', async () => {
    const el = await show(assistant('[docs](https://example.invalid/docs)'));
    const a = el.querySelector('a')!;
    expect(a.getAttribute('target')).toBe('_blank');
    expect(a.getAttribute('rel')).toBe('noopener noreferrer');
  });
});

describe('a tool call', () => {
  const [edit] = buildTranscript([
    line({ type: 'assistant', message: { content: [{ type: 'tool_use', id: 't1', name: 'Edit', input: {
      file_path: '/project/a.js', old_string: 'const a = 1;\nconst b = 2;', new_string: 'const a = 1;\nconst b = 3;\nconst c = 4;',
    } }] } }),
    line({ type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 't1', content: 'The file was updated' }] } }),
  ]);

  it('is one line until opened', async () => {
    const onToggle = vi.fn();
    const el = await show(edit, false, onToggle);
    const row = el.querySelector('button.ti-fold')!;
    expect(row.textContent).toContain('Edit');
    expect(row.textContent).toContain('/project/a.js');
    expect(el.querySelector('.ti-diffstat')?.textContent).toBe('+2-1');
    expect(el.querySelector('.ti-tool-body')).toBeNull();
    await act(async () => { (row as HTMLButtonElement).click(); });
    expect(onToggle).toHaveBeenCalledWith(edit.key);
  });

  it('opens to the diff it made', async () => {
    const el = await show(edit, true);
    const diff = [...el.querySelectorAll('.ti-diff > div')].map(d => d.textContent);
    expect(diff).toEqual([' const a = 1;', '-const b = 2;', '+const b = 3;', '+const c = 4;']);
  });

  const write = (content: string) => buildTranscript([
    line({ type: 'assistant', message: { content: [{ type: 'tool_use', id: 'w1', name: 'Write', input: { file_path: '/project/big.txt', content } }] } }),
  ])[0];
  const shownDiff = (el: HTMLElement) => [...el.querySelectorAll('.ti-diff > div:not(.ti-diff-more)')].map(d => d.textContent!.slice(1));

  it('opens to a big Write cut at 20k characters, as tool output is, saying how much is left out', async () => {
    // 3000 lines of 9 characters and a newline: the first 2000 are 20k characters
    const lines = Array.from({ length: 3000 }, (_, i) => `line ${String(i).padStart(4, '0')}`);
    const el = await show(write(lines.join('\n')), true);
    expect(el.querySelector('.ti-diffstat')?.textContent).toBe('+3000');
    expect(shownDiff(el)).toEqual(lines.slice(0, 2000));
    expect(el.querySelector('.ti-diff-more')?.textContent).toBe('… 10000 more characters');
  });

  it('cuts a diff inside a line longer than the rest of its room', async () => {
    const el = await show(write('x'.repeat(50_000)), true);
    expect(shownDiff(el)).toEqual(['x'.repeat(20_000)]);
    expect(el.querySelector('.ti-diff-more')?.textContent).toBe('… 30000 more characters');
  });

  it('shows a small diff whole', async () => {
    const el = await show(edit, true);
    expect(el.querySelector('.ti-diff-more')).toBeNull();
  });

  it('shows a failed call\'s error without opening', async () => {
    const [bash] = buildTranscript([
      line({ type: 'assistant', message: { content: [{ type: 'tool_use', id: 't2', name: 'Bash', input: { command: 'exit 3' } }] } }),
      line({ type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 't2', content: 'Exit code 3', is_error: true }] } }),
    ]);
    const el = await show(bash);
    expect(el.querySelector('.ti-tool-error')).not.toBeNull();
    expect(el.querySelector('.ti-tool-error-line')?.textContent).toBe('Exit code 3');
  });
});

it('shows my message as mine, as plain text', async () => {
  const el = await show({ kind: 'userText', key: 'u', text: 'fix **it** <b>now</b>' });
  expect(el.querySelector('.ti-user-bubble')?.textContent).toBe('fix **it** <b>now</b>');
  expect(el.querySelector('b')).toBeNull();
});
