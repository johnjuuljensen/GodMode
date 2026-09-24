/**
 * The hub contract's types come from generated/hub-types.ts, which tools/GodMode.TypeGen writes from
 * GodMode.Shared on every build of GodMode.Server. Change the C# models, not that file.
 * Only types the client makes up itself live here.
 */

export type * from './generated/hub-types';

// --- Claude output (parsed client-side from raw JSON, uses our own casing) ---

export interface ClaudeContentItem {
  type: string;
  summary: string;
  formattedJson: string;
  isExpanded: boolean;
  toolName?: string | null;
  toolFilePath?: string | null;
  toolOldString?: string | null;
  toolNewString?: string | null;
  toolCommand?: string | null;
  toolDescription?: string | null;
  toolContent?: string | null;
  isError: boolean;
}

export interface ClaudeMessage {
  type: string;
  subtype?: string | null;
  typeDisplay: string;
  isUserMessage: boolean;
  typeInitial: string;
  summary: string;
  contentItems: ClaudeContentItem[];
  hasContentItems: boolean;
  hasErrorContent: boolean;
  contentSummary: string;
  formattedJson: string;
  isToolOnly: boolean;
  textOnlyContentSummary: string;
}
