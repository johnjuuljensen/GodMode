/**
 * Claude's output stream, parsed in one place: each raw JSON line into a ClaudeMessage, and a run of
 * messages into the transcript a reader sees (TranscriptItem), where each tool result sits inside
 * the tool call it answers and a subagent's output inside the Agent/Task call that started it.
 */
import type { ClaudeMessage, ClaudeContentItem, ToolInput } from './types';

const MAX_SUMMARY = 200;
const MAX_CONTENT_SUMMARY = 300;
const MAX_TOOL_SUMMARY = 80;

const clip = (text: string, max: number) => (text.length > max ? text.slice(0, max) + '...' : text);
const firstLine = (text: string) => text.split('\n').find(l => l.trim() !== '') ?? '';

// The stream's JSON is claude's, and not typed: read it through these narrowing helpers
type Json = Record<string, unknown>;
const asObject = (v: unknown): Json | null => (v && typeof v === 'object' && !Array.isArray(v) ? v as Json : null);
const asString = (v: unknown): string | null => (typeof v === 'string' ? v : null);

export function parseClaudeMessage(rawJson: string): ClaudeMessage {
  let root: Json | null;
  try {
    root = asObject(JSON.parse(rawJson));
  } catch {
    root = null;
  }
  if (!root) {
    return {
      type: 'error', subtype: null, typeDisplay: 'error', isUserMessage: false, parentToolUseId: null,
      summary: clip(rawJson, MAX_SUMMARY), isError: true, contentItems: [], contentSummary: '',
    };
  }

  const type = asString(root.type) ?? 'unknown';
  const subtype = asString(root.subtype);
  const contentItems = extractContentItems(root, type);
  return {
    type,
    subtype,
    typeDisplay: subtype ? `${type}:${subtype}` : type,
    isUserMessage: type === 'user',
    parentToolUseId: asString(root.parent_tool_use_id),
    summary: extractSummary(root, type, subtype),
    isError: type === 'error' || (type === 'result' && root.is_error === true),
    contentItems,
    contentSummary: buildContentSummary(contentItems),
  };
}

function extractSummary(root: Json, type: string, subtype: string | null): string {
  switch (type) {
    case 'system': {
      const parts: string[] = [];
      if (subtype) parts.push(subtype);
      const sid = asString(root.session_id);
      if (subtype === 'init' && sid) parts.push(`session: ${clip(sid, 8)}`);
      const model = asString(root.model);
      if (model) parts.push(model);
      const description = asString(root.description) ?? asString(root.summary);
      if (description) parts.push(clip(firstLine(description), MAX_SUMMARY));
      return parts.join(' | ');
    }
    // The final result is shown whole: it is the turn's answer, usually markdown
    case 'result': return asString(root.result) ?? '';
    case 'error': return clip(asString(root.error) ?? asString(root.message) ?? '', MAX_SUMMARY);
    default: return '';
  }
}

function extractContentItems(root: Json, type: string): ClaudeContentItem[] {
  if (type !== 'user' && type !== 'assistant') return [];
  const content = asObject(root.message)?.content;
  // A user message sent as a plain string
  if (typeof content === 'string') return [textItem(content)];
  if (!Array.isArray(content)) return [];
  return content.map(parseContentItem).filter((i): i is ClaudeContentItem => i !== null);
}

const textItem = (text: string): ClaudeContentItem => ({ type: 'text', summary: text, text, isError: false });

/** A tool_result's content: a string, or an array of blocks whose text blocks are joined. */
function toolResultText(content: unknown): string {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content
    .map(block => {
      const b = asObject(block);
      if (!b) return '';
      if (b.type === 'text') return asString(b.text) ?? '';
      return `[${asString(b.type) ?? 'content'}]`;
    })
    .filter(Boolean)
    .join('\n');
}

function readToolInput(value: unknown): ToolInput {
  const input = asObject(value) ?? {};
  const str = (key: string) => asString(input[key]) ?? undefined;
  const edits = Array.isArray(input.edits)
    ? input.edits.map(asObject).filter((e): e is Json => e !== null)
      .map(e => ({ oldString: asString(e.old_string) ?? '', newString: asString(e.new_string) ?? '' }))
    : undefined;
  return {
    filePath: str('file_path') ?? str('notebook_path'),
    oldString: str('old_string'),
    newString: str('new_string'),
    content: str('content'),
    edits,
    command: str('command'),
    description: str('description'),
    pattern: str('pattern'),
    path: str('path'),
    query: str('query'),
    url: str('url'),
    prompt: str('prompt'),
    subagentType: str('subagent_type'),
  };
}

/** The one line that says what a tool call does: its file, pattern, command or query. */
export function toolSummary(input: ToolInput): string {
  const target = input.filePath
    ?? (input.pattern !== undefined ? (input.path ? `${input.pattern} in ${input.path}` : input.pattern) : undefined)
    ?? (input.command !== undefined ? firstLine(input.command) : undefined)
    ?? input.query
    ?? input.url
    ?? input.description
    ?? (input.prompt !== undefined ? firstLine(input.prompt) : undefined)
    ?? '';
  return clip(target, MAX_TOOL_SUMMARY);
}

function parseContentItem(value: unknown): ClaudeContentItem | null {
  const el = asObject(value);
  const type = el && asString(el.type);
  if (!el || !type) return null;

  switch (type) {
    case 'text': return textItem(asString(el.text) ?? '');
    case 'thinking':
    case 'redacted_thinking': {
      // Redacted thinking carries only a signature (or opaque data): its text is empty
      const text = type === 'thinking' ? asString(el.thinking) ?? '' : '';
      return { type: 'thinking', summary: text, text, isError: false };
    }
    case 'tool_use': {
      const name = asString(el.name) ?? 'tool';
      const toolInput = readToolInput(el.input);
      const target = toolSummary(toolInput);
      return {
        type, summary: target ? `${name} → ${target}` : name, isError: false,
        toolUseId: asString(el.id) ?? undefined, toolName: name, toolInput,
      };
    }
    case 'tool_result': {
      const text = toolResultText(el.content);
      return {
        type, summary: clip(firstLine(text), MAX_CONTENT_SUMMARY), text,
        isError: el.is_error === true, toolUseId: asString(el.tool_use_id) ?? undefined,
      };
    }
    default: return { type, summary: type, isError: false };
  }
}

function buildContentSummary(items: ClaudeContentItem[]): string {
  return items
    .filter(item => item.type !== 'thinking')
    .map(item => {
      if (item.type === 'text') return item.summary;
      if (item.type === 'tool_result' && item.isError) return `[ERROR] ${item.summary}`;
      return `[${item.type}] ${item.summary}`;
    })
    .join('\n');
}

// ── The transcript ─────────────────────────────────────────────

export interface ToolResult {
  text: string;
  summary: string;
  isError: boolean;
}

export interface ToolCallItem {
  kind: 'toolCall';
  key: string;
  id: string;
  name: string;
  summary: string;
  input: ToolInput;
  result?: ToolResult;
  isError: boolean;
  /** What a subagent started by this call said and did */
  children: TranscriptItem[];
}

/** One row of a transcript. `key` is stable for the life of the transcript, for React. */
export type TranscriptItem =
  | { kind: 'userText'; key: string; text: string }
  | { kind: 'assistantText'; key: string; text: string }
  | { kind: 'thinking'; key: string; text: string }
  | ToolCallItem
  | { kind: 'system'; key: string; label: string; summary: string; isError: boolean }
  | { kind: 'result'; key: string; summary: string; isError: boolean };

/** The transcript of a run of messages, from scratch. */
export function buildTranscript(messages: readonly ClaudeMessage[]): TranscriptItem[] {
  return createTranscriptBuilder()(messages);
}

/**
 * A transcript kept up to date as messages are appended: given the same array with more on the
 * end, it adds only what is new, and every item that did not change is the same object as before
 * (so a memoized row does not re-render). Any other array is built from scratch.
 */
export function createTranscriptBuilder(): (messages: readonly ClaudeMessage[]) => TranscriptItem[] {
  let seen: readonly ClaudeMessage[] = [];
  let items: TranscriptItem[] = [];
  // Every tool call by its id, with the call it is nested in (none at the top level)
  let calls = new Map<string, { item: ToolCallItem; parentId: string | null }>();
  let topIndex = new Map<string, number>();

  return messages => {
    const extends_ = seen.length > 0 && messages.length >= seen.length
      && messages[0] === seen[0] && messages[seen.length - 1] === seen[seen.length - 1];
    if (!extends_) {
      seen = [];
      items = [];
      calls = new Map();
      topIndex = new Map();
    }
    if (messages.length === seen.length) {
      seen = messages;
      return items;
    }

    const next = items.slice();

    /** Swaps a call for its updated copy, copying each call it is nested in up to the top. */
    const replace = (old: ToolCallItem, updated: ToolCallItem) => {
      const entry = calls.get(old.id)!;
      calls.set(old.id, { ...entry, item: updated });
      if (entry.parentId === null) {
        next[topIndex.get(old.id)!] = updated;
        return;
      }
      const parent = calls.get(entry.parentId)!.item;
      replace(parent, { ...parent, children: parent.children.map(c => (c === old ? updated : c)) });
    };

    const add = (item: TranscriptItem, parentId: string | null) => {
      const parent = parentId !== null ? calls.get(parentId)?.item : undefined;
      if (item.kind === 'toolCall') calls.set(item.id, { item, parentId: parent ? parentId : null });
      if (parent) {
        replace(parent, { ...parent, children: [...parent.children, item] });
      } else {
        if (item.kind === 'toolCall') topIndex.set(item.id, next.length);
        next.push(item);
      }
    };

    for (let i = seen.length; i < messages.length; i++) {
      const message = messages[i];
      const parentId = message.parentToolUseId ?? null;
      for (const item of messageItems(message, `${i}`)) {
        if (item.kind === 'attach') {
          const call = calls.get(item.toolUseId)?.item;
          if (call) {
            replace(call, { ...call, result: item.result, isError: item.result.isError });
          } else {
            // A result whose call is not held, such as one before a tail's first line
            add({
              kind: 'toolCall', key: item.key, id: item.toolUseId, name: 'Tool result', summary: item.result.summary,
              input: {}, result: item.result, isError: item.result.isError, children: [],
            }, parentId);
          }
        } else {
          add(item, parentId);
        }
      }
    }

    seen = messages;
    items = next;
    return items;
  };
}

type Attach = { kind: 'attach'; key: string; toolUseId: string; result: ToolResult };

function messageItems(message: ClaudeMessage, key: string): (TranscriptItem | Attach)[] {
  switch (message.type) {
    case 'user':
    case 'assistant':
      return message.contentItems.flatMap((c, j): (TranscriptItem | Attach)[] => {
        const itemKey = `${key}.${j}`;
        switch (c.type) {
          case 'text':
            return c.text?.trim() ? [{ kind: message.type === 'user' ? 'userText' : 'assistantText', key: itemKey, text: c.text }] : [];
          case 'thinking':
            // Redacted thinking has nothing to show
            return c.text?.trim() ? [{ kind: 'thinking', key: itemKey, text: c.text }] : [];
          case 'tool_use':
            return [{
              kind: 'toolCall', key: itemKey, id: c.toolUseId ?? itemKey, name: c.toolName ?? 'tool',
              summary: toolSummary(c.toolInput ?? {}), input: c.toolInput ?? {}, isError: false, children: [],
            }];
          case 'tool_result':
            return [{
              kind: 'attach', key: itemKey, toolUseId: c.toolUseId ?? itemKey,
              result: { text: c.text ?? '', summary: c.summary, isError: c.isError },
            }];
          default:
            return [{ kind: 'system', key: itemKey, label: c.type, summary: c.summary, isError: false }];
        }
      });
    case 'result':
      return [{ kind: 'result', key, summary: message.summary, isError: message.isError }];
    default:
      return [{ kind: 'system', key, label: message.typeDisplay, summary: message.summary, isError: message.isError }];
  }
}
