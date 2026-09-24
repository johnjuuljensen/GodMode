/**
 * The hub contract's types come from generated/hub-types.ts, which tools/GodMode.TypeGen writes from
 * GodMode.Shared on every build of GodMode.Server. Change the C# models, not that file.
 * Only types the client makes up itself live here.
 */

export type * from './generated/hub-types';

// --- Claude output (parsed client-side from raw JSON by parseMessage.ts, uses our own casing) ---

/** The fields of a tool call's input that the transcript shows. */
export interface ToolInput {
  filePath?: string;
  oldString?: string;
  newString?: string;
  content?: string;
  edits?: { oldString: string; newString: string }[];
  command?: string;
  description?: string;
  pattern?: string;
  path?: string;
  query?: string;
  url?: string;
  prompt?: string;
  subagentType?: string;
}

export interface ClaudeContentItem {
  /** text, thinking (redacted thinking too, with empty text), tool_use, tool_result, or claude's own name for another block */
  type: string;
  /** One line: a text block's text, a tool call's name and target, a tool result's first line */
  summary: string;
  /** The whole text of a text, thinking or tool_result block */
  text?: string;
  isError: boolean;
  /** A tool_use's id, or the id of the tool_use a tool_result answers */
  toolUseId?: string;
  toolName?: string;
  toolInput?: ToolInput;
}

export interface ClaudeMessage {
  type: string;
  subtype?: string | null;
  typeDisplay: string;
  isUserMessage: boolean;
  /** Set on a subagent's messages: the id of the Agent/Task call that started it */
  parentToolUseId: string | null;
  summary: string;
  /** An error line, or a result that reports an error */
  isError: boolean;
  contentItems: ClaudeContentItem[];
  contentSummary: string;
}
