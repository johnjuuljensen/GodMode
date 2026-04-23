/**
 * Question detection — deterministic rule (see issue #131).
 *
 * The only reliable signal in Claude's stream-json output is that the last
 * `type="text"` content block of the assistant message preceding a `result`
 * event ends with '?' (after TrimEnd) iff that turn is a question. Fuzzy
 * heuristics ("would you like", numbered lists, etc.) produce false positives
 * on final summaries, so we don't use them.
 *
 * Layers, in priority order:
 *   1. Structured AskUserQuestion tool_use (see parseMessage.ts:extractQuestionData)
 *   2. Last text block of an assistant message, trimmed, ends with '?'
 *   3. Server-side CurrentQuestion from ProjectStatus (authoritative, also based on rule 2)
 */
import type { ClaudeMessage, QuestionOptionData } from '../signalr/types';

export interface QuestionState {
  isActive: boolean;
  text: string | null;
  options: QuestionOptionData[];
  header: string | null;
}

export const emptyQuestion: QuestionState = {
  isActive: false,
  text: null,
  options: [],
  header: null,
};

/**
 * Detects a question from a newly received message (layers 1 & 2).
 * Returns a QuestionState if a question is detected, or null to clear/ignore.
 */
export function detectQuestionFromMessage(
  message: ClaudeMessage,
  _currentQuestion: QuestionState,
  _lastInputSentAt: number,
  isDismissed?: boolean,
): QuestionState | null {
  // Don't re-detect if user dismissed this project's question
  if (isDismissed) return null;

  // Layer 1: Structured AskUserQuestion
  if (message.isQuestion && message.questionOptions.length > 0) {
    return {
      isActive: true,
      text: message.questionText ?? null,
      options: message.questionOptions,
      header: message.questionHeader ?? null,
    };
  }

  // Layer 2: Last text block of this assistant message ends with '?'
  if (message.type === 'assistant') {
    const lastText = getLastTextBlock(message);
    if (lastText !== null && endsWithQuestionMark(lastText)) {
      return {
        isActive: true,
        text: lastText,
        options: parseTextOptions(lastText),
        header: null,
      };
    }
  }

  // Result means turn is over — don't clear the question here.
  // The status update (onStatusChanged) will handle re-detection or clearing
  // based on whether the project is in WaitingInput state.
  // Clearing here would race with the status update and lose the question.

  return null;
}

/**
 * Detects a question from server-side project status (layer 3).
 */
export function detectQuestionFromStatus(
  state: string,
  currentQuestion: string | null | undefined,
  projectName: string,
  existingQuestion: QuestionState,
  lastInputSentAt: number,
  outputMessages: ClaudeMessage[],
): QuestionState | null {
  // Don't override an active structured question
  if (existingQuestion.isActive && existingQuestion.options.length > 0) return null;

  // Don't re-detect right after user sent input
  if (Date.now() - lastInputSentAt < 5000) return null;

  const isWaiting = state === 'WaitingInput' || state === 'Idle';
  if (!isWaiting) return null;

  // Prefer server-side CurrentQuestion (server runs the same deterministic rule).
  // Fallback: scan recent output for an assistant message whose last text block ends with '?'.
  let questionText = currentQuestion ?? null;
  if (!questionText) {
    questionText = getLastAssistantQuestion(outputMessages);
  }

  if (questionText) {
    return {
      isActive: true,
      text: questionText,
      options: parseTextOptions(questionText),
      header: projectName,
    };
  }

  return null;
}

function getLastAssistantQuestion(messages: ClaudeMessage[]): string | null {
  for (let i = messages.length - 1; i >= 0; i--) {
    const msg = messages[i];
    if (msg.type === 'user') break;
    if (msg.type !== 'assistant') continue;
    const lastText = getLastTextBlock(msg);
    if (lastText !== null && endsWithQuestionMark(lastText)) return lastText;
  }
  return null;
}

/**
 * Returns the text of the last `type === 'text'` content item in the message,
 * or null if there isn't one.
 */
export function getLastTextBlock(message: ClaudeMessage): string | null {
  for (let i = message.contentItems.length - 1; i >= 0; i--) {
    const item = message.contentItems[i];
    if (item.type === 'text') return item.summary;
  }
  return null;
}

/** Deterministic question check: trimmed text ends with '?'. */
export function endsWithQuestionMark(text: string): boolean {
  if (!text) return false;
  const trimmed = text.replace(/\s+$/, '');
  return trimmed.length > 0 && trimmed.charCodeAt(trimmed.length - 1) === 0x3f; // '?'
}

/**
 * True iff this message should surface as a question (structured tool_use or
 * assistant text ending with '?'). Used where a single bool decision is enough.
 */
export function isQuestionMessage(message: ClaudeMessage): boolean {
  if (message.isQuestion && message.questionOptions.length > 0) return true;
  if (message.type !== 'assistant') return false;
  const lastText = getLastTextBlock(message);
  return lastText !== null && endsWithQuestionMark(lastText);
}

/** Parses inline options from question text (y/n, numbered lists, etc.) */
export function parseTextOptions(text: string): QuestionOptionData[] {
  const trimmed = text.trim();

  // (y/n) or (yes/no)
  if (/\(y(?:es)?\/n(?:o)?\)\s*$/i.test(trimmed)) {
    return [{ label: 'Yes', description: null }, { label: 'No', description: null }];
  }

  // (a/b/c) style
  const parenMatch = trimmed.match(/\(([^)]+\/[^)]+)\)\s*$/);
  if (parenMatch) {
    return parenMatch[1].split('/').map(p => ({ label: p.trim(), description: null }));
  }

  // Numbered list: 1. Option one\n2. Option two
  const numbered = [...trimmed.matchAll(/^\s*\d+[.)]\s+(.+)$/gm)];
  if (numbered.length >= 2) {
    return numbered.map(m => ({ label: m[1].trim(), description: null }));
  }

  // Lettered list: A) Option one\nB) Option two or A. Option one
  const lettered = [...trimmed.matchAll(/^\s*([A-Za-z])[.)]\s+(.+)$/gm)];
  if (lettered.length >= 2) {
    return lettered.map(m => ({ label: `${m[1].toUpperCase()}) ${m[2].trim()}`, description: null }));
  }

  // Bullet list: - Option one\n- Option two
  const bullets = [...trimmed.matchAll(/^\s*[-*]\s+(.+)$/gm)];
  if (bullets.length >= 2) {
    return bullets.map(m => ({ label: m[1].trim(), description: null }));
  }

  return [];
}
