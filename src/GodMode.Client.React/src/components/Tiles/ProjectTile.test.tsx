// @vitest-environment jsdom
/**
 * A tile previews the transcript as the transcript reads it: a tool's result inside its call, not a
 * message of mine, and a long reply or error clipped to what the tile has room for.
 */
import { afterEach, expect, it, vi } from 'vitest';
import { render, type Rendered } from '../../test/render';
import { project } from '../../test/fakeHub';
import { parseClaudeMessage } from '../../signalr/parseMessage';
import type { ClaudeMessage } from '../../signalr/types';
import { ProjectTile } from './ProjectTile';

vi.mock('../../signalr/hub', () => ({ GodModeHub: class {} }));
vi.mock('../../services/hostApi', () => ({
  waitUntilReady: async () => {},
  fetchServers: async () => [],
  subscribeEvents: () => {},
  getHubUrl: (serverId: string) => `http://test/${serverId}`,
  getHubOptions: () => ({}),
}));

let view: Rendered | undefined;
afterEach(() => { view?.unmount(); view = undefined; });

const line = (json: object) => parseClaudeMessage(JSON.stringify(json));
const toolUse = (id: string, name: string, input: object) => line({ type: 'assistant', message: { content: [{ type: 'tool_use', id, name, input }] } });
const result = (id: string, content: string, isError = false) =>
  line({ type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: id, content, is_error: isError }] } });

async function tile(messages: ClaudeMessage[]) {
  view = await render(
    <ProjectTile project={project('p1', 'work', 'Running', new Date().toISOString())} serverId="A" messages={messages} isLoading={false} isSelected={false} onSelect={() => {}} />,
  );
  return view.container;
}

const LONG = 'All done. '.repeat(500);

const turn = [
  line({ type: 'system', subtype: 'init', session_id: 'abc', model: 'claude' }),
  line({ type: 'user', message: { content: 'List the files' } }),
  toolUse('t1', 'Bash', { command: 'ls' }),
  result('t1', 'a.txt\nb.txt\n' + 'c.txt\n'.repeat(5000)),
  toolUse('t2', 'Edit', { file_path: '/project/a.txt', old_string: 'a', new_string: 'b' }),
  result('t2', 'String to replace not found in file', true),
  line({ type: 'assistant', message: { content: [{ type: 'text', text: LONG }] } }),
  line({ type: 'result', subtype: 'success', result: LONG, is_error: false }),
];

it("shows a tool's result inside its call, and only my message as mine", async () => {
  const el = await tile(turn);
  expect([...el.querySelectorAll('.tile-msg-user')].map(m => m.textContent)).toEqual(['List the files']);
  expect([...el.querySelectorAll('.tile-tool')].map(t => [t.querySelector('.tile-tool-name')?.textContent, t.querySelector('.tile-tool-summary')?.textContent]))
    .toEqual([['Bash', 'ls'], ['Edit', '/project/a.txt']]);
  expect(el.textContent).not.toContain('b.txt');
  expect(el.textContent).not.toContain('tool_result');
});

it("shows a failed call's error on a line under it, and a call that ran as done", async () => {
  const el = await tile(turn);
  const [bash, edit] = el.querySelectorAll('.tile-tool');
  expect(bash.querySelector('.tile-tool-status')?.getAttribute('title')).toBe('Done');
  expect(bash.querySelector('.tile-tool-error-line')).toBeNull();
  expect(edit.classList).toContain('tile-tool-error');
  expect(edit.querySelector('.tile-tool-error-line')?.textContent).toBe('String to replace not found in file');
});

it('keeps session bookkeeping out, as the simple view does', async () => {
  const el = await tile(turn);
  expect(el.querySelector('.tile-status')).toBeNull();
  expect(el.textContent).not.toContain('init');
});

it('clips a long reply to what a tile shows, and a failed result to its first line', async () => {
  const el = await tile([...turn.slice(0, -1), line({ type: 'result', subtype: 'error', result: `## Failed\n\n${LONG}`, is_error: true })]);
  const [reply] = el.querySelectorAll('.tile-msg-other');
  expect(reply.textContent).toBe(LONG.slice(0, 400));
  expect(el.querySelector('.tile-status-error .tile-status-text')?.textContent).toBe('Failed');
});
