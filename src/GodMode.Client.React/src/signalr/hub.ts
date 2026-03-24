/**
 * SignalR connection manager for GodMode.Server.
 * Mirrors the IProjectHub/IProjectHubClient contract from GodMode.Shared.
 */
import * as signalR from '@microsoft/signalr';
import type { ProjectSummary, ProjectStatus, ProjectRootInfo, ProfileInfo, McpRegistrySearchResult, McpServerDetail, McpServerConfig, RootTemplate, RootPreview, RootGenerationRequest, SharedRootPreview, InferenceStatus } from './types';
import { parseClaudeMessage } from './parseMessage';
import type { ClaudeMessage } from './types';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export interface HubCallbacks {
  onOutputReceived?: (projectId: string, message: ClaudeMessage) => void;
  onStatusChanged?: (projectId: string, status: ProjectStatus) => void;
  onProjectCreated?: (status: ProjectStatus) => void;
  onCreationProgress?: (projectId: string, message: string) => void;
  onProjectDeleted?: (projectId: string) => void;
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

  async connect(serverUrl: string, accessToken?: string): Promise<void> {
    if (this.connection) {
      await this.disconnect();
    }

    const hubUrl = serverUrl.replace(/\/$/, '') + '/hubs/projects';

    let builder = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, accessToken ? { accessTokenFactory: () => accessToken } : {})
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning);

    this.connection = builder.build();

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

  // --- MCP Server Discovery & Configuration ---

  async searchMcpServers(query: string, pageSize: number = 20, page: number = 1): Promise<McpRegistrySearchResult> {
    return await this.connection!.invoke('SearchMcpServers', query, pageSize, page);
  }

  async getMcpServerDetail(qualifiedName: string): Promise<McpServerDetail | null> {
    return await this.connection!.invoke('GetMcpServerDetail', qualifiedName);
  }

  async addMcpServer(
    serverName: string, config: McpServerConfig, targetLevel: string,
    profileName?: string | null, rootName?: string | null, actionName?: string | null,
  ): Promise<void> {
    await this.connection!.invoke('AddMcpServer', serverName, config, targetLevel, profileName, rootName, actionName);
  }

  async removeMcpServer(
    serverName: string, targetLevel: string,
    profileName?: string | null, rootName?: string | null, actionName?: string | null,
  ): Promise<void> {
    await this.connection!.invoke('RemoveMcpServer', serverName, targetLevel, profileName, rootName, actionName);
  }

  async getEffectiveMcpServers(
    profileName: string, rootName: string, actionName?: string | null,
  ): Promise<Record<string, McpServerConfig>> {
    return await this.connection!.invoke('GetEffectiveMcpServers', profileName, rootName, actionName);
  }

  // --- Profile Management ---

  async createProfile(profileName: string, description?: string | null): Promise<void> {
    await this.connection!.invoke('CreateProfile', profileName, description ?? null);
  }

  async updateProfileDescription(profileName: string, description?: string | null): Promise<void> {
    await this.connection!.invoke('UpdateProfileDescription', profileName, description ?? null);
  }

  // --- Root Creation & Management ---

  async listRootTemplates(): Promise<RootTemplate[]> {
    return await this.connection!.invoke('ListRootTemplates');
  }

  async previewRootFromTemplate(templateName: string, parameters: Record<string, string>): Promise<RootPreview> {
    return await this.connection!.invoke('PreviewRootFromTemplate', templateName, parameters);
  }

  async generateRootWithLlm(request: RootGenerationRequest): Promise<RootPreview> {
    return await this.connection!.invoke('GenerateRootWithLlm', request);
  }

  async createRoot(profileName: string, rootName: string, preview: RootPreview): Promise<void> {
    await this.connection!.invoke('CreateRoot', profileName, rootName, preview);
  }

  async getRootPreview(profileName: string, rootName: string): Promise<RootPreview> {
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

  async previewImportFromGit(repoUrl: string, subPath?: string | null, gitRef?: string | null): Promise<SharedRootPreview> {
    return await this.connection!.invoke('PreviewImportFromGit', repoUrl, subPath ?? null, gitRef ?? null);
  }

  async installSharedRoot(preview: SharedRootPreview, localName?: string | null): Promise<void> {
    await this.connection!.invoke('InstallSharedRoot', preview, localName ?? null);
  }

  async uninstallSharedRoot(rootName: string): Promise<void> {
    await this.connection!.invoke('UninstallSharedRoot', rootName);
  }

  async getInferenceStatus(): Promise<InferenceStatus> {
    return await this.connection!.invoke('GetInferenceStatus');
  }

  async configureInferenceApiKey(apiKey: string): Promise<InferenceStatus> {
    return await this.connection!.invoke('ConfigureInferenceApiKey', apiKey);
  }

  async restartServer(): Promise<void> {
    await this.connection!.invoke('RestartServer');
  }
}
