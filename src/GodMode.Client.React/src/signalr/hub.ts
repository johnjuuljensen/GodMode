/**
 * SignalR connection manager.
 * Connects to the local MAUI proxy which relays to the actual GodMode.Server.
 */
import * as signalR from '@microsoft/signalr';
import type { ProjectSummary, ProjectStatus, ProjectRootInfo, ProfileInfo } from './types';
import { parseClaudeMessage } from './parseMessage';
import type { ClaudeMessage } from './types';
import { getBaseUrl } from '../services/api';

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

  /**
   * Connect to a host via the local MAUI proxy.
   * @param hostId The host ID (from GET /hosts) used as ?serverId for the relay.
   */
  async connect(hostId: string): Promise<void> {
    if (this.connection) {
      await this.disconnect();
    }

    const baseUrl = getBaseUrl();
    if (!baseUrl) throw new Error('Base URL not configured — is this running inside MAUI?');

    const hubUrl = `${baseUrl}/?serverId=${encodeURIComponent(hostId)}`;

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        skipNegotiation: true,
        transport: signalR.HttpTransportType.WebSockets,
      })
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
}
