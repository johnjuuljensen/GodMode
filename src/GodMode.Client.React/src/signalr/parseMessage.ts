/**
 * Parses raw JSON from Claude's output stream into a ClaudeMessage.
 * Mirrors the eager parsing logic from GodMode.Shared/Models/ClaudeMessage.cs.
 */
import type { ClaudeMessage, ClaudeContentItem, QuestionOptionData } from './types';

const MAX_SUMMARY = 200;
const MAX_CONTENT_SUMMARY = 300;

export function parseClaudeMessage(rawJson: string): ClaudeMessage {
  try {
    const root = JSON.parse(rawJson);
    const type: string = root.type ?? 'unknown';
    const subtype: string | null = root.subtype ?? null;
    const typeDisplay = subtype ? `${type}:${subtype}` : type;
    const isUserMessage = type === 'user';
    const typeInitial = ({ system: 'S', user: 'U', assistant: 'A', result: 'R', error: '!' })[type] ?? '?';
    const contentItems = extractContentItems(root, type);
    const hasContentItems = contentItems.length > 0;
    const hasErrorContent = contentItems.some(i => i.isError);
    const isToolOnly = hasContentItems && contentItems.every(i => i.type === 'tool_use' || i.type === 'tool_result');
    const question = extractQuestionData(root);

    return {
      type,
      subtype,
      typeDisplay,
      isUserMessage,
      typeInitial,
      summary: extractSummary(root, type, subtype),
      contentItems,
      hasContentItems,
      hasErrorContent,
      contentSummary: buildContentSummary(contentItems),
      formattedJson: JSON.stringify(root, null, 2),
      isToolOnly,
      textOnlyContentSummary: contentItems.filter(i => i.type === 'text').map(i => i.summary).join('\n'),
      ...question,
    };
  } catch {
    return {
      type: 'error', subtype: null, typeDisplay: 'error', isUserMessage: false, typeInitial: '!',
      summary: rawJson.length > MAX_SUMMARY ? rawJson.slice(0, MAX_SUMMARY) + '...' : rawJson,
      contentItems: [], hasContentItems: false, hasErrorContent: false, contentSummary: '',
      formattedJson: rawJson, isToolOnly: false, textOnlyContentSummary: '',
      isQuestion: false, questionText: null, questionOptions: [], questionHeader: null,
    };
  }
}

function extractSummary(root: any, type: string, subtype: string | null): string {
  if (type === 'system') {
    const parts: string[] = [];
    if (subtype) parts.push(subtype);
    if (root.session_id) {
      const sid = String(root.session_id);
      parts.push(`session: ${sid.length > 8 ? sid.slice(0, 8) + '...' : sid}`);
    }
    if (root.model) parts.push(root.model);
    return parts.join(' | ');
  }
  if (type === 'result') {
    const text = root.result ?? '';
    return text.length > MAX_SUMMARY ? text.slice(0, MAX_SUMMARY) + '...' : text;
  }
  return '';
}

function extractContentItems(root: any, type: string): ClaudeContentItem[] {
  if (type !== 'user' && type !== 'assistant') return [];
  const content = root.message?.content;
  if (!Array.isArray(content)) return [];
  return content.map(parseContentItem).filter((i): i is ClaudeContentItem => i !== null);
}

function parseContentItem(el: any): ClaudeContentItem | null {
  const type: string = el.type;
  if (!type) return null;

  let summary: string;
  if (type === 'text') {
    summary = el.text ?? '';
  } else if (type === 'tool_use') {
    const parts: string[] = [];
    if (el.name) parts.push(el.name);
    const input = el.input;
    if (input?.file_path) parts.push(input.file_path);
    else if (input?.pattern) parts.push(`pattern: ${input.pattern}`);
    else if (input?.command) {
      const cmd = String(input.command);
      parts.push(cmd.length > 50 ? cmd.slice(0, 50) + '...' : cmd);
    } else if (input?.query) {
      const q = String(input.query);
      parts.push(q.length > 50 ? q.slice(0, 50) + '...' : q);
    }
    summary = parts.join(' \u2192 ');
  } else if (type === 'tool_result') {
    const text = typeof el.content === 'string' ? el.content : '';
    const firstLine = text.split('\n')[0];
    summary = firstLine.length > MAX_CONTENT_SUMMARY ? firstLine.slice(0, MAX_CONTENT_SUMMARY) + '...' : firstLine;
  } else {
    summary = type;
  }

  return {
    type,
    summary,
    formattedJson: JSON.stringify(el, null, 2),
    isExpanded: false,
    toolName: el.name ?? null,
    toolFilePath: el.input?.file_path ?? null,
    toolOldString: el.input?.old_string ?? null,
    toolNewString: el.input?.new_string ?? null,
    toolCommand: el.input?.command ?? null,
    toolDescription: el.input?.description ?? null,
    toolContent: el.input?.content ?? null,
    isError: type === 'tool_result' && el.is_error === true,
  };
}

function buildContentSummary(items: ClaudeContentItem[]): string {
  if (items.length === 0) return '';
  return items.map(item => {
    if (item.type === 'text') return item.summary;
    if (item.type === 'tool_result' && item.isError) return `[ERROR] ${item.summary}`;
    return `[${item.type}] ${item.summary}`;
  }).join('\n');
}

function extractQuestionData(root: any): {
  isQuestion: boolean;
  questionText: string | null;
  questionOptions: QuestionOptionData[];
  questionHeader: string | null;
} {
  const content = root.message?.content;
  if (!Array.isArray(content)) return { isQuestion: false, questionText: null, questionOptions: [], questionHeader: null };

  for (const item of content) {
    if (item.type !== 'tool_use' || item.name !== 'AskUserQuestion') continue;
    const input = item.input;
    if (!input) continue;

    const options: QuestionOptionData[] = Array.isArray(input.options)
      ? input.options.map((o: any) => ({ label: o.label ?? '', description: o.description ?? null }))
      : [];

    return {
      isQuestion: true,
      questionText: input.question ?? null,
      questionOptions: options,
      questionHeader: input.header ?? null,
    };
  }

  return { isQuestion: false, questionText: null, questionOptions: [], questionHeader: null };
}
