/**
 * TypeScript interfaces mirroring GodMode.Shared models and hub contracts.
 * Server uses PropertyNamingPolicy = null (PascalCase) and string enum serialization.
 */

// --- Enums (serialized as strings by JsonStringEnumConverter) ---

export type ProjectState = 'Idle' | 'Running' | 'WaitingInput' | 'Error' | 'Stopped';
export type HostState = 'Running' | 'Stopped' | 'Starting' | 'Stopping' | 'Unknown';

// --- Models (PascalCase properties matching server serialization) ---

export interface ProjectSummary {
  Id: string;
  Name: string;
  State: ProjectState;
  UpdatedAt: string;
  CurrentQuestion?: string | null;
  RootName?: string | null;
  ProfileName?: string | null;
}

export interface ProjectMetrics {
  InputTokens: number;
  OutputTokens: number;
  ToolCalls: number;
  Duration: string;
  CostEstimate: number;
}

export interface GitStatus {
  Branch?: string | null;
  LastCommit?: string | null;
  UncommittedChanges: number;
  UntrackedFiles: number;
}

export interface TestStatus {
  Total: number;
  Passed: number;
  Failed: number;
  LastRun?: string | null;
}

export interface ProjectStatus {
  Id: string;
  Name: string;
  State: ProjectState;
  CreatedAt: string;
  UpdatedAt: string;
  CurrentQuestion?: string | null;
  Metrics: ProjectMetrics;
  Git?: GitStatus | null;
  Tests?: TestStatus | null;
  OutputOffset: number;
  RootName?: string | null;
  ProfileName?: string | null;
}

export interface ProfileInfo {
  Name: string;
  Description?: string | null;
}

export interface CreateActionInfo {
  Name: string;
  Description?: string | null;
  InputSchema?: unknown | null;
  Model?: string | null;
}

export interface ProjectRootInfo {
  Name: string;
  Description?: string | null;
  Actions?: CreateActionInfo[] | null;
  ProfileName?: string | null;
}

export interface HostInfo {
  Id: string;
  Name: string;
  Type: string;
  State: HostState;
  Url?: string | null;
  Description?: string | null;
}

// --- Claude output (parsed client-side from raw JSON, uses our own casing) ---

export interface QuestionOptionData {
  label: string;
  description?: string | null;
}

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
  isQuestion: boolean;
  questionText?: string | null;
  questionOptions: QuestionOptionData[];
  questionHeader?: string | null;
}
