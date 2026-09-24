import { describe, expect, it } from 'vitest';
import fixture from '../test/fixtures/output.jsonl?raw';
import { buildTranscript, createTranscriptBuilder, parseClaudeMessage, type ToolCallItem, type TranscriptItem } from './parseMessage';

// A real session (scrubbed): Glob, Read, Grep, Edit, Write, two Bash calls (one failing), an Agent
// call whose subagent reads a file, redacted thinking, and a markdown summary
const lines = fixture.split('\n').filter(l => l.trim());
const messages = lines.map(parseClaudeMessage);
const transcript = buildTranscript(messages);

/** Every item, subagents' included */
const all = (items: TranscriptItem[]): TranscriptItem[] =>
  items.flatMap(i => (i.kind === 'toolCall' ? [i, ...all(i.children)] : [i]));
const count = (items: TranscriptItem[]) =>
  all(items).reduce<Record<string, number>>((acc, i) => ({ ...acc, [i.kind]: (acc[i.kind] ?? 0) + 1 }), {});
const calls = all(transcript).filter((i): i is ToolCallItem => i.kind === 'toolCall');
const call = (name: string) => calls.find(c => c.name === name)!;

const rawResults = lines.flatMap(l => {
  const content = JSON.parse(l).message?.content;
  return Array.isArray(content) ? content.filter((c: { type: string }) => c.type === 'tool_result') : [];
});

describe('the transcript of a real session', () => {
  it('has the expected items', () => {
    expect(count(transcript)).toEqual({
      system: 7, // init, rate limits, task progress
      userText: 2, // my prompt, and the prompt the Agent call gave its subagent
      assistantText: 2,
      toolCall: 9, // eight, and the subagent's Read
      result: 1,
    });
  });

  it('shows no tool result as a user message', () => {
    const userItems = all(transcript).filter(i => i.kind === 'userText');
    expect(userItems.map(i => i.kind === 'userText' && i.text.slice(0, 15))).toEqual(['Do these steps ', 'Read the file /']);
  });

  it('attaches every tool_result to the tool call it answers', () => {
    expect(rawResults).toHaveLength(9);
    expect(calls).toHaveLength(9);
    for (const r of rawResults) expect(calls.find(c => c.id === r.tool_use_id)?.result).toBeDefined();
    expect(calls.filter(c => c.name === 'Tool result')).toEqual([]);
  });

  it('nests the subagent under its Agent call', () => {
    const agent = call('Agent');
    expect(agent.children.map(c => c.kind === 'toolCall' ? `${c.name}: ${c.summary}` : c.kind))
      .toEqual(['userText', 'Read: /project/README.md']);
    expect(transcript.filter(i => i.kind === 'toolCall').map(i => i.kind === 'toolCall' && i.name))
      .toEqual(['Glob', 'Read', 'Grep', 'Edit', 'Write', 'Bash', 'Bash', 'Agent']);
  });

  it('summarises an array-valued tool_result, the Agent report', () => {
    const report = call('Agent').result!;
    expect(report.summary).not.toBe('');
    expect(report.text).toContain('# demo');
  });

  it('marks the failed Bash call, and only it, as an error', () => {
    expect(calls.filter(c => c.isError).map(c => `${c.name}: ${c.summary} -> ${c.result?.summary}`))
      .toEqual(['Bash: node -e "process.exit(3)" -> Exit code 3']);
  });

  it('hides redacted thinking', () => {
    expect(messages.flatMap(m => m.contentItems).filter(c => c.type === 'thinking')).toHaveLength(1);
    expect(all(transcript).filter(i => i.kind === 'thinking')).toEqual([]);
  });

  it('keeps what an Edit changed', () => {
    expect(call('Edit').input).toMatchObject({ filePath: '/project/src/math.js', oldString: 'module.exports = { add, mul };' });
    expect(call('Write').input.content).toContain('# Math Helpers Notes');
  });

  it('keeps no pretty-printed JSON on a message', () => {
    expect(JSON.stringify(messages)).not.toContain('formattedJson');
  });
});

describe('building a transcript as lines arrive', () => {
  it('matches building it at once, keeping every item that did not change', () => {
    const build = createTranscriptBuilder();
    let previous: TranscriptItem[] = [];
    let held: typeof messages = [];
    for (const message of messages) {
      held = [...held, message];
      const items = build(held);
      // Only a call that got its result or a subagent step, and the new items, are new objects
      const changed = items.filter((item, i) => item !== previous[i]);
      expect(changed.length).toBeLessThanOrEqual(3);
      previous = items;
    }
    expect(previous).toEqual(transcript);
  });

  it('starts again when the messages are replaced', () => {
    const build = createTranscriptBuilder();
    build(messages);
    const replaced = messages.slice(0, 3);
    expect(build(replaced)).toEqual(buildTranscript(replaced));
  });

  it('shows a result whose call is not held as a call of its own', () => {
    const orphan = parseClaudeMessage(JSON.stringify({
      type: 'user', message: { content: [{ type: 'tool_result', tool_use_id: 'gone', content: 'late' }] },
    }));
    expect(buildTranscript([orphan])).toMatchObject([{ kind: 'toolCall', id: 'gone', result: { text: 'late' } }]);
  });
});
