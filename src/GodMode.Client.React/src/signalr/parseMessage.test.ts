import { describe, expect, it } from 'vitest';
import fixture from '../test/fixtures/output.jsonl?raw';
import { parseClaudeMessage } from './parseMessage';

// A real session (scrubbed): Glob, Read, Grep, Edit, Write, two Bash calls (one failing), an Agent
// call whose subagent reads a file, redacted thinking, and a markdown summary
const messages = fixture.split('\n').filter(Boolean).map(parseClaudeMessage);
const items = messages.flatMap(m => m.contentItems.map(item => ({ message: m, item })));

describe('parsing a real transcript (base behaviour)', () => {
  it('shows no tool result as a user message', () => {
    const toolResultUsers = messages.filter(m => m.isUserMessage && m.contentItems.every(i => i.type === 'tool_result'));
    expect(toolResultUsers.length).toBe(0);
  });

  it('attaches every tool_result to its tool call', () => {
    const unattached = items.filter(({ item }) => item.type === 'tool_result');
    expect(unattached.length).toBe(0);
  });

  it('summarises an array-valued tool_result', () => {
    const empty = items.filter(({ item }) => item.type === 'tool_result' && item.summary === '');
    expect(empty.length).toBe(0);
  });

  it('renders no redacted thinking', () => {
    const redacted = items.filter(({ item }) => item.type === 'thinking');
    expect(redacted.length).toBe(0);
  });
});
