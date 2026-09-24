/**
 * TypeScript interfaces mirroring GodMode.Shared models and hub contracts.
 * Server uses PropertyNamingPolicy = null (PascalCase) and string enum serialization.
 */

// --- Enums (serialized as strings by JsonStringEnumConverter) ---

export type ProjectState = 'Idle' | 'Running' | 'WaitingInput' | 'WaitingPermission' | 'Error' | 'Stopped';
export type ServerState = 'Running' | 'Stopped' | 'Starting' | 'Stopping' | 'Unknown';
/** What a project needs from the user, most urgent first (AttentionKind in GodMode.Shared). */
export type AttentionKind = 'Permission' | 'Question' | 'Error' | 'Review' | 'Finished';
export type PullRequestState = 'Draft' | 'Open' | 'Merged' | 'Closed';
export type PullRequestReview = 'None' | 'ChangesRequested' | 'Approved';

// --- Models (PascalCase properties matching server serialization) ---

export interface ProjectSummary {
  Id: string;
  Name: string;
  State: ProjectState;
  UpdatedAt: string;
  CurrentQuestion?: string | null;
  RootName?: string | null;
  ProfileName?: string | null;
  /** As ProjectStatus.PendingPermission. */
  PendingPermission?: PendingPermission | null;
  /** As ProjectStatus.PendingQuestion. */
  PendingQuestion?: PendingQuestion | null;
  /** As ProjectStatus.PullRequest. */
  PullRequest?: PullRequestStatus | null;
}

/**
 * The pull request a project's work became, as its root's status script last reported it
 * (PullRequestStatus in GodMode.Shared).
 */
export interface PullRequestStatus {
  Url: string;
  Number: number;
  State: PullRequestState;
  Review: PullRequestReview;
  /** When the server first saw this State and Review together; stable across server restarts. */
  ChangedAt: string;
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
  /** The byte offset in output.jsonl after its last line. */
  OutputOffset: number;
  RootName?: string | null;
  ProfileName?: string | null;
  /** Why the project is in Error: claude's last stderr lines before it exited, or an error result's text. */
  LastError?: string | null;
  /** The tool call waiting for the user to allow or deny it, while the project is WaitingPermission. */
  PendingPermission?: PendingPermission | null;
  /** The AskUserQuestion waiting for the user's answer, while the project is WaitingInput on it. */
  PendingQuestion?: PendingQuestion | null;
  /** The text of the last successful result: claude's own summary of its turn. */
  LastResult?: string | null;
  /** When LastResult came. */
  LastResultAt?: string | null;
  /** When CurrentQuestion was asked; only meaningful while it is set. */
  QuestionAt?: string | null;
  /** When the user last saw the result (MarkSeen, or a reply). A result after it is unseen. */
  SeenAt?: string | null;
  /** The pull request the project's work became; null when there is none or the root has no status script. */
  PullRequest?: PullRequestStatus | null;
}

/**
 * One project that needs the user, from GetAttention and AttentionChanged (AttentionItem in
 * GodMode.Shared). Per server: key it by server and ProjectId (IDs contain '/').
 */
export interface AttentionItem {
  ProjectId: string;
  ProjectName: string;
  /** The profile (account) the project belongs to. */
  Profile?: string | null;
  Root?: string | null;
  Kind: AttentionKind;
  /** When it started to need this; stable across server restarts. */
  Since: string;
  /** Plain text (no code blocks, about 500 characters at most): the question, permission summary, error, review or result. */
  Text: string;
  /** The tool call to allow or deny, when Kind is 'Permission'. */
  Permission?: PendingPermission | null;
  /** The AskUserQuestion with its options, when Kind is 'Question' and claude asked with the tool. */
  Question?: PendingQuestion | null;
  /** The project's pull request, when Kind is 'Review' or 'Finished' and it has one. */
  PullRequestUrl?: string | null;
}

/** A tool call claude holds until the user answers it with RespondToPermission (PendingPermission in GodMode.Shared). */
export interface PendingPermission {
  RequestId: string;
  ToolName: string;
  /** The tool's input as claude sent it. Show Summary rather than parsing this. */
  Input: unknown;
  /** One line saying what the call does, e.g. "Bash: git push origin feature/12-x". */
  Summary: string;
  RequestedAt: string;
}

/** The answer to a PendingPermission (PermissionDecision in GodMode.Shared). */
export interface PermissionDecision {
  Allow: boolean;
  /** Why it was denied; claude reads it. */
  Message?: string | null;
  /** The input to run the tool with instead. Only read when allowed. */
  UpdatedInput?: unknown;
}

/** Questions claude asked with AskUserQuestion, answered with AnswerQuestion (PendingQuestion in GodMode.Shared). */
export interface PendingQuestion {
  RequestId: string;
  Questions: QuestionItem[];
  RequestedAt: string;
}

export interface QuestionItem {
  /** The question; its answer is keyed by this text. */
  Question: string;
  Header?: string | null;
  Options: QuestionOption[];
  /** More than one option may be chosen; their labels are joined with ", ". */
  MultiSelect: boolean;
}

export interface QuestionOption {
  Label: string;
  Description?: string | null;
}

/** One replayed line of a project's output (OutputLine in GodMode.Shared). */
export interface OutputLine {
  /** The byte offset in output.jsonl just after this line: subscribe from it to get only what follows. */
  Offset: number;
  RawJson: string;
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

export interface ServerInfo {
  Id: string;
  Name: string;
  Type: string;
  State: ServerState;
  Url?: string | null;
  Description?: string | null;
}

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
