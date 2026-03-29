/**
 * TypeScript interfaces mirroring GodMode.Shared models and hub contracts.
 * Server uses PropertyNamingPolicy = null (PascalCase) and string enum serialization.
 */

// --- Enums (serialized as strings by JsonStringEnumConverter) ---

export type ProjectState = 'Idle' | 'Running' | 'WaitingInput' | 'Error' | 'Stopped';
export type ServerState = 'Running' | 'Stopped' | 'Starting' | 'Stopping' | 'Unknown';

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
  HasConfig?: boolean;
}

export interface ServerInfo {
  Id: string;
  Name: string;
  Type: string;
  State: ServerState;
  Url?: string | null;
  Description?: string | null;
}

// --- MCP Server models ---

export interface McpServerConfig {
  Command?: string | null;
  Args?: string[] | null;
  Env?: Record<string, string> | null;
  Url?: string | null;
  Type?: string | null;
}

export interface McpRegistryServer {
  QualifiedName: string;
  DisplayName: string;
  Description?: string | null;
  IconUrl?: string | null;
  Verified: boolean;
  UseCount: number;
  Remote: boolean;
  IsDeployed: boolean;
  Homepage?: string | null;
}

export interface McpRegistryPagination {
  CurrentPage: number;
  PageSize: number;
  TotalPages: number;
  TotalCount: number;
}

export interface McpRegistrySearchResult {
  Servers: McpRegistryServer[];
  Pagination: McpRegistryPagination;
}

export interface McpServerConnection {
  Type: string;
  DeploymentUrl?: string | null;
  configSchema?: unknown | null;
}

export interface McpServerTool {
  Name: string;
  Description?: string | null;
}

export interface McpServerDetail {
  QualifiedName: string;
  DisplayName: string;
  Description?: string | null;
  IconUrl?: string | null;
  Remote: boolean;
  DeploymentUrl?: string | null;
  Connections?: McpServerConnection[] | null;
  Tools?: McpServerTool[] | null;
}

// --- Root Creation & Sharing models ---

export interface RootTemplate {
  Name: string;
  DisplayName: string;
  Description: string;
  Icon?: string | null;
  Parameters?: RootTemplateParameter[] | null;
}

export interface RootTemplateParameter {
  Key: string;
  Title: string;
  Description?: string | null;
  DefaultValue?: string | null;
  Required: boolean;
}

export interface RootPreview {
  Files: Record<string, string>;
  ValidationError?: string | null;
}

export interface RootGenerationRequest {
  UserInstruction: string;
  CurrentFiles?: Record<string, string> | null;
  SchemaFields?: SchemaFieldDefinition[] | null;
}

export interface SchemaFieldDefinition {
  Key: string;
  Title: string;
  FieldType: string;
  IsRequired: boolean;
  IsMultiline: boolean;
  EnumValues?: string[] | null;
}

export interface RootManifest {
  Name: string;
  DisplayName: string;
  Description?: string | null;
  Author?: string | null;
  Version?: string | null;
  Platforms?: string[] | null;
  Tags?: string[] | null;
  Source?: string | null;
  MinGodModeVersion?: string | null;
}

export interface SharedRootPreview {
  Manifest: RootManifest;
  Files: Record<string, string>;
  ScriptHashes?: Record<string, string> | null;
}

export interface InferenceStatus {
  IsConfigured: boolean;
  Provider?: string | null;
  Model?: string | null;
  Error?: string | null;
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

// ── MCP Registry Types ──

export interface McpRegistryServer {
  qualifiedName: string;
  displayName: string;
  description?: string | null;
  homepage?: string | null;
  useCount?: number;
  isVerified?: boolean;
  createdAt?: string;
}

export interface McpRegistrySearchResult {
  servers: McpRegistryServer[];
  pagination: McpRegistryPagination;
}

export interface McpRegistryPagination {
  currentPage: number;
  pageSize: number;
  totalPages: number;
  totalCount: number;
}

export interface McpServerDetail {
  qualifiedName: string;
  displayName: string;
  description?: string | null;
  homepage?: string | null;
  connections?: McpServerConnection[];
  tools?: McpServerTool[];
}

export interface McpServerConnection {
  type: string;
  url?: string | null;
  configSchema?: unknown;
}

export interface McpServerTool {
  name: string;
  description?: string | null;
}

export interface McpServerConfig {
  command?: string | null;
  args?: string[] | null;
  env?: Record<string, string> | null;
  url?: string | null;
  type?: string | null;
}

export interface InferenceStatus {
  IsConfigured: boolean;
  Provider?: string | null;
  Model?: string | null;
  Error?: string | null;
}

// ── Root Template Types ──

export interface RootTemplate {
  Name: string;
  DisplayName: string;
  Description: string;
  Icon?: string | null;
  Parameters?: RootTemplateParameter[] | null;
}

export interface RootTemplateParameter {
  Key: string;
  Title: string;
  Description?: string | null;
  DefaultValue?: string | null;
  Required: boolean;
}

export interface RootPreview {
  Files: Record<string, string>;
  ValidationError?: string | null;
}

export interface RootGenerationRequest {
  UserInstruction: string;
  CurrentFiles?: Record<string, string> | null;
  SchemaFields?: SchemaFieldDefinition[] | null;
}

export interface SchemaFieldDefinition {
  Key: string;
  Title: string;
  FieldType: string;
  IsRequired: boolean;
  IsMultiline: boolean;
  EnumValues?: string[] | null;
}

export interface RootManifest {
  Name: string;
  DisplayName: string;
  Description?: string | null;
  Author?: string | null;
  Version?: string | null;
  Platforms?: string[] | null;
  Tags?: string[] | null;
  Source?: string | null;
  MinGodModeVersion?: string | null;
}

export interface SharedRootPreview {
  Manifest: RootManifest;
  Files: Record<string, string>;
  ScriptHashes?: Record<string, string> | null;
}
