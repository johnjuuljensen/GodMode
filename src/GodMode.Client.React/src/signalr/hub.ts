/**
 * SignalR connection manager.
 * Supports two connection modes:
 *  - MAUI proxy relay (via local WebSocket proxy)
 *  - Direct connection to GodMode.Server (standalone browser)
 * The caller provides the hub URL and connection options via IHostApi.
 */
import * as signalR from '@microsoft/signalr';
import type { ProjectSummary, ProjectStatus, ProjectRootInfo, ProfileInfo, McpServerConfig, RootPreview, SharedRootPreview, ChatResponseMessage, WebhookInfo, OAuthProviderStatus, ScheduleInfo, ScheduleConfig } from './types';
import { parseClaudeMessage } from './parseMessage';
import type { ClaudeMessage } from './types';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export interface HubCallbacks {
  onOutputReceived?: (projectId: string, message: ClaudeMessage) => void;
  onStatusChanged?: (projectId: string, status: ProjectStatus) => void;
  onProjectCreated?: (status: ProjectStatus) => void;
  onCreationProgress?: (projectId: string, message: string) => void;
  onProjectDeleted?: (projectId: string) => void;
  onProjectArchived?: (projectId: string) => void;
  onProjectRestored?: (project: ProjectSummary) => void;
  onChatResponse?: (message: ChatResponseMessage) => void;
  onRootsChanged?: () => void;
  onProfilesChanged?: () => void;
  onWebhooksChanged?: () => void;
  onOAuthStatusChanged?: (profileName: string) => void;
  onStateChanged?: (state: ConnectionState) => void;
}

export class GodModeHub {
  private connection: signalR.HubConnection | null = null;
  private callbacks: HubCallbacks = {};
  private _state: ConnectionState = 'disconnected';

  get state(): ConnectionState {
    return this._state;
  }

  private setState(state: ConnectionState) {
    this._state = state;
    this.callbacks.onStateChanged?.(state);
  }

  setCallbacks(callbacks: HubCallbacks) {
    this.callbacks = callbacks;
  }

  /**
   * Connect to a GodMode.Server SignalR hub.
   * @param hubUrl  Full URL (from IHostApi.getHubUrl)
   * @param options Connection options (from IHostApi.getHubOptions)
   */
  async connect(hubUrl: string, options: signalR.IHttpConnectionOptions = {}): Promise<void> {
    if (this.connection) {
      await this.disconnect();
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, options)
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    // Register server→client callbacks (IProjectHubClient)
    this.connection.on('OutputReceived', (projectId: string, rawJson: string) => {
      const message = parseClaudeMessage(rawJson);
      this.callbacks.onOutputReceived?.(projectId, message);
    });

    this.connection.on('StatusChanged', (projectId: string, status: ProjectStatus) => {
      this.callbacks.onStatusChanged?.(projectId, status);
    });

    this.connection.on('ProjectCreated', (status: ProjectStatus) => {
      this.callbacks.onProjectCreated?.(status);
    });

    this.connection.on('CreationProgress', (projectId: string, message: string) => {
      this.callbacks.onCreationProgress?.(projectId, message);
    });

    this.connection.on('ProjectDeleted', (projectId: string) => {
      this.callbacks.onProjectDeleted?.(projectId);
    });

    this.connection.on('ProjectArchived', (projectId: string) => {
      this.callbacks.onProjectArchived?.(projectId);
    });

    this.connection.on('ProjectRestored', (project: ProjectSummary) => {
      this.callbacks.onProjectRestored?.(project);
    });

    this.connection.on('ChatResponse', (message: ChatResponseMessage) => {
      this.callbacks.onChatResponse?.(message);
    });

    this.connection.on('RootsChanged', () => {
      this.callbacks.onRootsChanged?.();
    });

    this.connection.on('ProfilesChanged', () => {
      this.callbacks.onProfilesChanged?.();
    });

    this.connection.on('WebhooksChanged', () => {
      this.callbacks.onWebhooksChanged?.();
    });

    this.connection.on('OAuthStatusChanged', (profileName: string) => {
      this.callbacks.onOAuthStatusChanged?.(profileName);
    });

    this.connection.onreconnecting(() => this.setState('reconnecting'));
    this.connection.onreconnected(() => this.setState('connected'));
    this.connection.onclose(() => this.setState('disconnected'));

    this.setState('connecting');
    await this.connection.start();
    this.setState('connected');
  }

  async disconnect(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
      this.setState('disconnected');
    }
  }

  // --- Client→Server methods (IProjectHub) ---

  async listProfiles(): Promise<ProfileInfo[]> {
    return await this.connection!.invoke('ListProfiles');
  }

  async listProjectRoots(): Promise<ProjectRootInfo[]> {
    return await this.connection!.invoke('ListProjectRoots');
  }

  async listProjects(): Promise<ProjectSummary[]> {
    return await this.connection!.invoke('ListProjects');
  }

  async getStatus(projectId: string): Promise<ProjectStatus> {
    return await this.connection!.invoke('GetStatus', projectId);
  }

  async createProject(
    profileName: string,
    projectRootName: string,
    actionName: string | null,
    inputs: Record<string, unknown>,
  ): Promise<ProjectStatus> {
    return await this.connection!.invoke('CreateProject', profileName, projectRootName, actionName, inputs);
  }

  async sendInput(projectId: string, input: string): Promise<void> {
    await this.connection!.invoke('SendInput', projectId, input);
  }

  async stopProject(projectId: string): Promise<void> {
    await this.connection!.invoke('StopProject', projectId);
  }

  async resumeProject(projectId: string): Promise<void> {
    await this.connection!.invoke('ResumeProject', projectId);
  }

  async subscribeProject(projectId: string, outputOffset: number): Promise<void> {
    await this.connection!.invoke('SubscribeProject', projectId, outputOffset);
  }

  async unsubscribeProject(projectId: string): Promise<void> {
    await this.connection!.invoke('UnsubscribeProject', projectId);
  }

  async getMetricsHtml(projectId: string): Promise<string> {
    return await this.connection!.invoke('GetMetricsHtml', projectId);
  }

  async deleteProject(projectId: string, force: boolean = false): Promise<void> {
    await this.connection!.invoke('DeleteProject', projectId, force);
  }

  async archiveProject(projectId: string): Promise<void> {
    await this.connection!.invoke('ArchiveProject', projectId);
  }

  async unarchiveProject(projectId: string): Promise<void> {
    await this.connection!.invoke('UnarchiveProject', projectId);
  }

  async listArchivedProjects(): Promise<ProjectSummary[]> {
    return await this.connection!.invoke('ListArchivedProjects');
  }

  // --- MCP Server Management ---

  async addMcpServer(
    serverName: string,
    config: McpServerConfig,
    targetLevel: string,
    profileName?: string | null,
    rootName?: string | null,
    actionName?: string | null,
  ): Promise<void> {
    await this.connection!.invoke('AddMcpServer', serverName, config, targetLevel, profileName, rootName, actionName);
  }

  async removeMcpServer(
    serverName: string,
    targetLevel: string,
    profileName?: string | null,
    rootName?: string | null,
    actionName?: string | null,
  ): Promise<void> {
    await this.connection!.invoke('RemoveMcpServer', serverName, targetLevel, profileName, rootName, actionName);
  }

  async getEffectiveMcpServers(
    profileName: string,
    rootName: string,
    actionName?: string | null,
  ): Promise<Record<string, McpServerConfig>> {
    return await this.connection!.invoke('GetEffectiveMcpServers', profileName, rootName, actionName);
  }

  // --- Profile Management ---

  async createProfile(name: string, description?: string | null): Promise<void> {
    await this.connection!.invoke('CreateProfile', name, description);
  }

  async deleteProfile(name: string, deleteContents: boolean = false): Promise<void> {
    await this.connection!.invoke('DeleteProfile', name, deleteContents);
  }

  async updateProfileDescription(name: string, description?: string | null): Promise<void> {
    await this.connection!.invoke('UpdateProfileDescription', name, description);
  }

  // --- Root Management ---

  async createRoot(rootName: string, preview: RootPreview, profileName?: string | null): Promise<void> {
    await this.connection!.invoke('CreateRoot', rootName, preview, profileName);
  }

  async deleteRoot(profileName: string, rootName: string, force: boolean = false): Promise<void> {
    await this.connection!.invoke('DeleteRoot', profileName, rootName, force);
  }

  async getRootPreview(profileName: string, rootName: string): Promise<RootPreview | null> {
    return await this.connection!.invoke('GetRootPreview', profileName, rootName);
  }

  async updateRoot(profileName: string, rootName: string, preview: RootPreview): Promise<void> {
    await this.connection!.invoke('UpdateRoot', profileName, rootName, preview);
  }

  // --- Root Sharing ---

  async exportRoot(profileName: string, rootName: string): Promise<Uint8Array> {
    return await this.connection!.invoke('ExportRoot', profileName, rootName);
  }

  async previewImportFromBytes(packageBytes: Uint8Array): Promise<SharedRootPreview> {
    return await this.connection!.invoke('PreviewImportFromBytes', packageBytes);
  }

  async previewImportFromUrl(url: string): Promise<SharedRootPreview> {
    return await this.connection!.invoke('PreviewImportFromUrl', url);
  }

  async previewImportFromGit(gitUrl: string, path?: string | null, gitRef?: string | null): Promise<SharedRootPreview> {
    return await this.connection!.invoke('PreviewImportFromGit', gitUrl, path, gitRef);
  }

  async installSharedRoot(rootName: string, preview: SharedRootPreview): Promise<void> {
    await this.connection!.invoke('InstallSharedRoot', rootName, preview);
  }

  async uninstallSharedRoot(rootName: string): Promise<void> {
    await this.connection!.invoke('UninstallSharedRoot', rootName);
  }

  async exportManifest(): Promise<string> {
    return await this.connection!.invoke('ExportManifest');
  }

  // --- LLM Root Generation ---

  async generateRootWithLlm(request: { Instruction: string; CurrentFiles?: Record<string, string>; SchemaFields?: string[] }): Promise<RootPreview> {
    return await this.connection!.invoke('GenerateRootWithLlm', request);
  }

  // --- GodMode Chat ---

  async sendChatMessage(message: string): Promise<void> {
    await this.connection!.invoke('SendChatMessage', message);
  }

  async clearChatHistory(): Promise<void> {
    await this.connection!.invoke('ClearChatHistory');
  }

  // --- OAuth ---

  async getOAuthStatus(profileName: string): Promise<Record<string, OAuthProviderStatus>> {
    return await this.connection!.invoke('GetOAuthStatus', profileName);
  }

  async disconnectOAuthProvider(profileName: string, provider: string): Promise<void> {
    await this.connection!.invoke('DisconnectOAuthProvider', profileName, provider);
  }

  // --- Webhooks ---

  async listWebhooks(): Promise<WebhookInfo[]> {
    return await this.connection!.invoke('ListWebhooks');
  }

  async createWebhook(
    keyword: string,
    profileName: string,
    rootName: string,
    actionName?: string | null,
    description?: string | null,
    inputMapping?: Record<string, string> | null,
    staticInputs?: Record<string, unknown> | null,
  ): Promise<WebhookInfo> {
    return await this.connection!.invoke('CreateWebhook', keyword, profileName, rootName,
      actionName, description, inputMapping, staticInputs);
  }

  async deleteWebhook(keyword: string): Promise<void> {
    await this.connection!.invoke('DeleteWebhook', keyword);
  }

  async updateWebhook(
    keyword: string,
    description?: string | null,
    inputMapping?: Record<string, string> | null,
    staticInputs?: Record<string, unknown> | null,
    enabled?: boolean | null,
  ): Promise<WebhookInfo> {
    return await this.connection!.invoke('UpdateWebhook', keyword, description, inputMapping, staticInputs, enabled);
  }

  async regenerateWebhookToken(keyword: string): Promise<string> {
    return await this.connection!.invoke('RegenerateWebhookToken', keyword);
  }

  // ── Schedules ──

  async getSchedules(profileName: string): Promise<ScheduleInfo[]> {
    return await this.connection!.invoke('GetSchedules', profileName);
  }

  async createSchedule(profileName: string, name: string, config: ScheduleConfig): Promise<ScheduleInfo> {
    return await this.connection!.invoke('CreateSchedule', profileName, name, config);
  }

  async updateSchedule(profileName: string, name: string, config: ScheduleConfig): Promise<ScheduleInfo> {
    return await this.connection!.invoke('UpdateSchedule', profileName, name, config);
  }

  async deleteSchedule(profileName: string, name: string): Promise<void> {
    await this.connection!.invoke('DeleteSchedule', profileName, name);
  }

  async toggleSchedule(profileName: string, name: string, enabled: boolean): Promise<ScheduleInfo> {
    return await this.connection!.invoke('ToggleSchedule', profileName, name, enabled);
  }

  // ── Utility ──

  async checkCommand(command: string): Promise<string | null> {
    return await this.connection!.invoke('CheckCommand', command);
  }
}
