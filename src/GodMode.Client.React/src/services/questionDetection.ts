/**
 * Question detection logic — mirrors the 3-layer approach from ProjectViewModel.cs.
 *
 * Layer 1: Structured AskUserQuestion tool_use (parsed in parseMessage.ts)
 * Layer 2: Text-based heuristic on assistant messages
 * Layer 3: Server-side CurrentQuestion from ProjectStatus
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

  // Layer 2: Text-based question from assistant message
  if (message.type === 'assistant' && message.contentSummary && looksLikeQuestion(message.contentSummary)) {
    return {
      isActive: true,
      text: message.contentSummary,
      options: parseTextOptions(message.contentSummary),
      header: null,
    };
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

  // Try server-side CurrentQuestion first
  let questionText = currentQuestion ?? null;

  // Client-side fallback: check last assistant message
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
    if (msg.contentSummary && looksLikeQuestion(msg.contentSummary)) {
      return msg.contentSummary;
    }
  }
  return null;
}

/** Client-side heuristic: does this text look like a question? */
export function looksLikeQuestion(text: string): boolean {
  const trimmed = text.trim();

  // Contains a question mark (not just at end — options may follow the question)
  if (trimmed.includes('?')) return true;

  // (y/n) or (yes/no) style
  if (/\(y(?:es)?\/n(?:o)?\)\s*$/i.test(trimmed)) return true;

  // (a/b/c) style options
  if (/\([^)]+\/[^)]+\)\s*$/.test(trimmed)) return true;

  // Common question phrases
  const phrases = ['would you like', 'do you want', 'should i', 'shall i', 'which one', 'please choose', 'please select', 'multiple choice'];
  const lower = trimmed.toLowerCase();
  for (const phrase of phrases) {
    if (lower.includes(phrase)) return true;
  }

  return false;
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
