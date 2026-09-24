/**
 * SignalR connection manager.
 * Supports two connection modes:
 *  - MAUI proxy relay (via local WebSocket proxy)
 *  - Direct connection to GodMode.Server (standalone browser)
 * The caller provides the hub URL and connection options via IHostApi.
 */
import * as signalR from '@microsoft/signalr';
import type { ProjectSummary, ProjectStatus, ProjectRootInfo, ProfileInfo, OutputLine, PermissionDecision, AttentionItem } from './types';
import { parseClaudeMessage } from './parseMessage';
import type { ClaudeMessage } from './types';

export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/** A line of output, parsed, with the byte offset in output.jsonl just after it. */
export interface OutputMessage {
  offset: number;
  message: ClaudeMessage;
}

export interface HubCallbacks {
  /** A live line, after the subscription's replay is complete. */
  onOutputReceived?: (projectId: string, line: OutputMessage) => void;
  /** Replayed lines, covering output.jsonl from fromOffset to the last line's offset. */
  onOutputBatch?: (projectId: string, fromOffset: number, lines: OutputMessage[]) => void;
  /** The replay is done at offset; live lines follow. */
  onOutputReplayComplete?: (projectId: string, offset: number) => void;
  onStatusChanged?: (projectId: string, status: ProjectStatus) => void;
  /** The projects needing the user changed; items is the server's whole list, oldest first. */
  onAttentionChanged?: (items: AttentionItem[]) => void;
  onProjectCreated?: (status: ProjectStatus) => void;
  onCreationProgress?: (projectId: string, message: string) => void;
  onProjectDeleted?: (projectId: string) => void;
  onProjectArchived?: (projectId: string) => void;
  onProjectRestored?: (project: ProjectSummary) => void;
  onProfilesChanged?: () => void;
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
    this.connection.on('OutputReceived', (projectId: string, offset: number, rawJson: string) => {
      this.callbacks.onOutputReceived?.(projectId, { offset, message: parseClaudeMessage(rawJson) });
    });

    this.connection.on('OutputBatch', (projectId: string, fromOffset: number, lines: OutputLine[]) => {
      this.callbacks.onOutputBatch?.(projectId, fromOffset,
        lines.map(l => ({ offset: l.Offset, message: parseClaudeMessage(l.RawJson) })));
    });

    this.connection.on('OutputReplayComplete', (projectId: string, offset: number) => {
      this.callbacks.onOutputReplayComplete?.(projectId, offset);
    });

    this.connection.on('StatusChanged', (projectId: string, status: ProjectStatus) => {
      this.callbacks.onStatusChanged?.(projectId, status);
    });

    this.connection.on('AttentionChanged', (items: AttentionItem[]) => {
      this.callbacks.onAttentionChanged?.(items);
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

    this.connection.on('ProfilesChanged', () => {
      this.callbacks.onProfilesChanged?.();
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

  /** Answers the project's PendingPermission: the tool call runs, or claude is told it was denied. */
  async respondToPermission(projectId: string, requestId: string, decision: PermissionDecision): Promise<void> {
    await this.connection!.invoke('RespondToPermission', projectId, requestId, decision);
  }

  /** Answers the project's PendingQuestion: each question's text to the chosen label or the user's own text. */
  async answerQuestion(projectId: string, requestId: string, answers: Record<string, string>): Promise<void> {
    await this.connection!.invoke('AnswerQuestion', projectId, requestId, answers);
  }

  /** Every project on this server that needs the user, oldest first. */
  async getAttention(): Promise<AttentionItem[]> {
    return await this.connection!.invoke('GetAttention');
  }

  /** The user has seen the project's last result: it is no longer 'Finished'. */
  async markSeen(projectId: string): Promise<void> {
    await this.connection!.invoke('MarkSeen', projectId);
  }

  /**
   * Answers the project whether claude runs or not: input to a running one (denying a pending
   * permission with it, or answering a single pending question), else a resume and then the input.
   * Resolves once a resumed claude has started its session; rejects if it fails to.
   */
  async replyAndResume(projectId: string, text: string): Promise<void> {
    await this.connection!.invoke('ReplyAndResume', projectId, text);
  }

  async stopProject(projectId: string): Promise<void> {
    await this.connection!.invoke('StopProject', projectId);
  }

  async resumeProject(projectId: string): Promise<void> {
    await this.connection!.invoke('ResumeProject', projectId);
  }

  /**
   * Replays the project's output from fromOffset (the offset of the last line held, 0 for all, or
   * -N for the last N turns), then streams it live. Resolves once the replay is complete.
   */
  async subscribeProject(projectId: string, fromOffset: number): Promise<void> {
    await this.connection!.invoke('SubscribeProject', projectId, fromOffset);
  }

  async unsubscribeProject(projectId: string): Promise<void> {
    await this.connection!.invoke('UnsubscribeProject', projectId);
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

  // ── Utility ──

  async checkCommand(command: string): Promise<string | null> {
    return await this.connection!.invoke('CheckCommand', command);
  }
}
