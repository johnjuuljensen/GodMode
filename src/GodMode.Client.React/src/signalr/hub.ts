/**
 * SignalR connection manager.
 * Supports two connection modes:
 *  - MAUI proxy relay (via local WebSocket proxy)
 *  - Direct connection to GodMode.Server (standalone browser)
 * The caller provides the hub URL and connection options via IHostApi.
 */
import * as signalR from '@microsoft/signalr';
import type {
  ProjectSummary, ProjectStatus, ProjectRootInfo, ProfileInfo, PermissionDecision, PermissionDetail, AttentionItem,
  IProjectHub, IProjectHubClient,
} from './types';
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
  /**
   * Replayed lines, covering output.jsonl from fromOffset to the last line's offset, for the subscription
   * subscriptionId, in the file's generation.
   */
  onOutputBatch?: (projectId: string, subscriptionId: string, generation: string, fromOffset: number, lines: OutputMessage[]) => void;
  /** The replay for subscriptionId is done at offset, in generation; live lines follow. */
  onOutputReplayComplete?: (projectId: string, subscriptionId: string, generation: string, offset: number) => void;
  onStatusChanged?: (projectId: string, status: ProjectStatus) => void;
  /** The projects needing the user changed; items is the server's whole list, oldest first. */
  onAttentionChanged?: (items: AttentionItem[]) => void;
  onProjectCreated?: (status: ProjectStatus) => void;
  onCreationProgress?: (projectId: string, message: string) => void;
  onProjectDeleted?: (projectId: string) => void;
  onStateChanged?: (state: ConnectionState) => void;
}

/**
 * How long to wait before each retry of a lost connection, by attempt; the last repeats for as long as
 * it takes. A phone that sleeps loses its connection every time, so a retry never gives up.
 */
export const RETRY_DELAYS_MS = [0, 1000, 2000, 5000, 10000, 20000, 30000];

/**
 * How long an attempt may be in flight before a wake gives up on it: one made before the page slept
 * can hang until the network gives up on it, and a wake stops it and starts again.
 */
export const HUNG_ATTEMPT_MS = 5000;

/** One connection and its retries: how many since it was lost, the next one's timer, and the attempt in flight. */
interface Link {
  readonly connection: signalR.HubConnection;
  retries: number;
  timer: ReturnType<typeof setTimeout> | null;
  /** When the attempt in flight started (Date.now()), null when none is. */
  attemptSince: number | null;
  /** A retry was asked for during the attempt in flight: the next one is at once. */
  wanted: boolean;
}

export class GodModeHub {
  private connection: signalR.HubConnection | null = null;
  /** The retry state of this.connection, its own: one left behind by disconnect() cannot touch the next. */
  private link: Link | null = null;
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

    // No withAutomaticReconnect: its loop gives up, and cannot be told to retry now. A lost
    // connection closes, and retry() starts the same connection again until it connects
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, options)
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    this.connection = connection;

    // Register server→client callbacks, each typed by its IProjectHubClient method
    this.on(connection, 'OutputReceived', (projectId, offset, rawJson) => {
      this.callbacks.onOutputReceived?.(projectId, { offset, message: parseClaudeMessage(rawJson) });
    });

    this.on(connection, 'OutputBatch', (projectId, subscriptionId, generation, fromOffset, lines) => {
      this.callbacks.onOutputBatch?.(projectId, subscriptionId, generation, fromOffset,
        lines.map(l => ({ offset: l.Offset, message: parseClaudeMessage(l.RawJson) })));
    });

    this.on(connection, 'OutputReplayComplete', (projectId, subscriptionId, generation, offset) => {
      this.callbacks.onOutputReplayComplete?.(projectId, subscriptionId, generation, offset);
    });

    this.on(connection, 'StatusChanged', (projectId, status) => {
      this.callbacks.onStatusChanged?.(projectId, status);
    });

    this.on(connection, 'AttentionChanged', (items) => {
      this.callbacks.onAttentionChanged?.(items);
    });

    this.on(connection, 'ProjectCreated', (status) => {
      this.callbacks.onProjectCreated?.(status);
    });

    this.on(connection, 'CreationProgress', (projectId, message) => {
      this.callbacks.onCreationProgress?.(projectId, message);
    });

    this.on(connection, 'ProjectDeleted', (projectId) => {
      this.callbacks.onProjectDeleted?.(projectId);
    });

    const link: Link = { connection, retries: 0, timer: null, attemptSince: null, wanted: false };
    this.link = link;

    // Closed by disconnect(), the connection is no longer this.connection. Else it was lost, once
    // connected: an attempt that fails, or is stopped by retryNow, closes none that was open
    connection.onclose(() => {
      if (this.link !== link || this._state !== 'connected') return;
      link.retries = 0;
      link.wanted = false;
      this.setState('reconnecting');
      this.scheduleRetry(link);
    });

    this.setState('connecting');
    try {
      await this.attempt(link);
    } catch (err) {
      // A server down when the page loads is retried as one that was lost; the caller hears it failed
      if (this.link === link) {
        this.setState('reconnecting');
        this.scheduleRetry(link);
      }
      throw err;
    }
    if (this.link === link) this.setState('connected');
  }

  async disconnect(): Promise<void> {
    const connection = this.connection;
    const link = this.link;
    if (connection) {
      this.connection = null;
      this.link = null;
      if (link) this.cancelRetry(link);
      await connection.stop();
      this.setState('disconnected');
    }
  }

  /**
   * Retries a lost connection now rather than when its wait is over: the page woke, or the network is
   * back. An attempt in flight longer than HUNG_ATTEMPT_MS is stopped, and the next one made at once.
   */
  retryNow() {
    const link = this.link;
    // A first connect in flight is retried too: it can hang as well
    if (!link || (this._state !== 'reconnecting' && this._state !== 'connecting')) return;
    if (link.attemptSince !== null) {
      link.wanted = true;
      if (Date.now() - link.attemptSince > HUNG_ATTEMPT_MS) {
        console.warn('[hub] abandoning an attempt in flight since', new Date(link.attemptSince).toISOString());
        link.connection.stop().catch(err => console.warn('[hub] stopping a hung attempt failed:', err));
      }
      return;
    }
    this.cancelRetry(link);
    void this.retry(link);
  }

  private scheduleRetry(link: Link) {
    this.cancelRetry(link);
    const delay = link.wanted ? 0 : RETRY_DELAYS_MS[Math.min(link.retries, RETRY_DELAYS_MS.length - 1)];
    link.wanted = false;
    link.timer = setTimeout(() => { link.timer = null; void this.retry(link); }, delay);
  }

  private cancelRetry(link: Link) {
    if (link.timer !== null) clearTimeout(link.timer);
    link.timer = null;
  }

  /** Starts the connection, noting when, so a wake can tell an attempt that hangs. */
  private async attempt(link: Link) {
    link.attemptSince = Date.now();
    try {
      await link.connection.start();
    } finally {
      link.attemptSince = null;
    }
  }

  private async retry(link: Link) {
    // Only this.connection, and only once it is down: start() on one connecting or connected throws
    if (this.link !== link || link.connection.state !== signalR.HubConnectionState.Disconnected) return;
    link.retries++;
    try {
      await this.attempt(link);
      if (this.link === link) this.setState('connected');
    } catch (err) {
      console.warn(`[hub] retry ${link.retries} failed:`, err);
      if (this.link === link) this.scheduleRetry(link);
    }
  }

  // --- The generated contract: each call is checked against IProjectHub or IProjectHubClient ---

  /** Invokes the hub method named, with its parameters and result as IProjectHub declares them. */
  private invoke<M extends keyof IProjectHub>(method: M, ...args: Parameters<IProjectHub[M]>): ReturnType<IProjectHub[M]> {
    return this.connection!.invoke(method, ...args) as ReturnType<IProjectHub[M]>;
  }

  /** Handles the server's call of the IProjectHubClient method named; the handler's parameters take its types. */
  private on<M extends keyof IProjectHubClient>(connection: signalR.HubConnection, method: M, handler: IProjectHubClient[M]) {
    connection.on(method, handler);
  }

  // --- Client→Server methods (IProjectHub) ---

  async listProfiles(): Promise<ProfileInfo[]> {
    return await this.invoke('ListProfiles');
  }

  async listProjectRoots(): Promise<ProjectRootInfo[]> {
    return await this.invoke('ListProjectRoots');
  }

  async listProjects(): Promise<ProjectSummary[]> {
    return await this.invoke('ListProjects');
  }

  async getStatus(projectId: string): Promise<ProjectStatus> {
    return await this.invoke('GetStatus', projectId);
  }

  async createProject(
    profileName: string,
    projectRootName: string,
    actionName: string | null,
    inputs: Record<string, unknown>,
  ): Promise<ProjectStatus> {
    return await this.invoke('CreateProject', profileName, projectRootName, actionName, inputs);
  }

  async sendInput(projectId: string, input: string): Promise<void> {
    await this.invoke('SendInput', projectId, input);
  }

  /** Answers the project's PendingPermission: the tool call runs, or claude is told it was denied. */
  async respondToPermission(projectId: string, requestId: string, decision: PermissionDecision): Promise<void> {
    await this.invoke('RespondToPermission', projectId, requestId, decision);
  }

  /** Everything the project's pending permission request would run, to show before it is allowed. */
  async getPermissionDetail(projectId: string, requestId: string): Promise<PermissionDetail> {
    return await this.invoke('GetPermissionDetail', projectId, requestId);
  }

  /** Answers the project's PendingQuestion: each question's text to the chosen label or the user's own text. */
  async answerQuestion(projectId: string, requestId: string, answers: Record<string, string>): Promise<void> {
    await this.invoke('AnswerQuestion', projectId, requestId, answers);
  }

  /** Every project on this server that needs the user, oldest first. */
  async getAttention(): Promise<AttentionItem[]> {
    return await this.invoke('GetAttention');
  }

  /** The user has seen the project's last result: it is no longer 'Finished', nor 'Review' until its pull request changes. */
  async markSeen(projectId: string): Promise<void> {
    await this.invoke('MarkSeen', projectId);
  }

  /**
   * Answers the project whether claude runs or not: input to a running one (denying a pending
   * permission with it, or answering a single pending question), else a resume and then the input.
   * Resolves once a resumed claude has started its session; rejects if it fails to.
   */
  async replyAndResume(projectId: string, text: string): Promise<void> {
    await this.invoke('ReplyAndResume', projectId, text);
  }

  async stopProject(projectId: string): Promise<void> {
    await this.invoke('StopProject', projectId);
  }

  async resumeProject(projectId: string): Promise<void> {
    await this.invoke('ResumeProject', projectId);
  }

  /**
   * Replays the project's output from fromOffset (the offset of the last line held, 0 for all, or
   * -N for the last N turns), then streams it live. Resolves once the replay is complete. The
   * replay's batches and complete carry subscriptionId back; generation is the one fromOffset is
   * in (null when nothing is held), and in any other the server replays from 0.
   */
  async subscribeProject(projectId: string, fromOffset: number, subscriptionId: string, generation: string | null): Promise<void> {
    await this.invoke('SubscribeProject', projectId, fromOffset, subscriptionId, generation);
  }

  async unsubscribeProject(projectId: string): Promise<void> {
    await this.invoke('UnsubscribeProject', projectId);
  }

  async deleteProject(projectId: string, force: boolean = false): Promise<void> {
    await this.invoke('DeleteProject', projectId, force);
  }

  // ── Utility ──

  async checkCommand(command: string): Promise<string | null> {
    return await this.invoke('CheckCommand', command);
  }
}
