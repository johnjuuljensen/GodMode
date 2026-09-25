/**
 * A GodModeHub stand-in that serves fixed lists and records what the client sends. Tests drive the
 * server's pushes through `callbacks`, and the connection's life through drop(), reconnect() and
 * failConnect. Each test file still mocks '../signalr/hub' and '../services/hostApi' itself (vi.mock
 * is hoisted per file).
 */
import type { ConnectionState, HubCallbacks, OutputMessage } from '../signalr/hub';
import type {
  PermissionDecision, ProjectSummary, ProjectRootInfo, ProfileInfo, ProjectState, ProjectStatus, ServerInfo,
} from '../signalr/types';
import { parseClaudeMessage } from '../signalr/parseMessage';
import { useAppStore, type ServerConnection } from '../store';

/** An assistant line whose text is `at <offset>`, with the byte offset after it. */
export const line = (offset: number): OutputMessage => ({
  offset,
  message: parseClaudeMessage(JSON.stringify({ type: 'assistant', message: { content: [{ type: 'text', text: `at ${offset}` }] } })),
});

/** The text of each message, as `line` wrote it: `at <offset>`. */
export const texts = (messages: { contentItems: { text?: string }[] }[] | undefined) =>
  (messages ?? []).map(m => m.contentItems[0]?.text);

/**
 * One SubscribeProject the server received. Nothing is answered until the test says: its batches and
 * its complete land when, and in the order, the test calls them, after newer subscriptions if it likes.
 */
export class Replay {
  private readonly hub: FakeHub;
  readonly projectId: string;
  readonly fromOffset: number;
  constructor(hub: FakeHub, projectId: string, fromOffset: number) {
    this.hub = hub;
    this.projectId = projectId;
    this.fromOffset = fromOffset;
  }

  /** Replayed lines, covering output.jsonl from fromOffset to the last line's offset. */
  batch(fromOffset: number, offsets: number[]) {
    this.hub.callbacks.onOutputBatch?.(this.projectId, fromOffset, offsets.map(line));
  }

  complete(offset: number) {
    this.hub.callbacks.onOutputReplayComplete?.(this.projectId, offset);
  }

  /** The whole answer: the lines in one batch from fromOffset (none if there are none), then complete at the last. */
  answer(fromOffset: number, offsets: number[]) {
    if (offsets.length > 0) this.batch(fromOffset, offsets);
    this.complete(offsets[offsets.length - 1] ?? fromOffset);
  }
}

export class FakeHub {
  callbacks: HubCallbacks = {};
  projects: ProjectSummary[];
  roots: ProjectRootInfo[];
  state: ConnectionState = 'disconnected';
  /** When set, connect() rejects, as against a server that cannot be reached. */
  failConnect = false;
  /** Every text sent through ReplyAndResume, and every AnswerQuestion's answers, in order. */
  replies: { projectId: string; text: string }[] = [];
  answers: { projectId: string; requestId: string; answers: Record<string, string> }[] = [];
  /** Every RespondToPermission's decision, MarkSeen and CreateProject, in order. */
  decisions: { projectId: string; requestId: string; decision: PermissionDecision }[] = [];
  seen: string[] = [];
  created: { rootName: string; actionName: string | null; inputs: Record<string, unknown> }[] = [];
  /** Every SubscribeProject and UnsubscribeProject that reached the server, in order. */
  subscriptions: { projectId: string; fromOffset: number }[] = [];
  /** The same subscriptions, each to be answered when the test says. Kept by resetCalls. */
  replays: Replay[] = [];
  unsubscriptions: string[] = [];
  /** How often each call reached the server. */
  calls = { connect: 0, listProjects: 0, getAttention: 0, retryNow: 0 };
  constructor(projects: ProjectSummary[], roots: ProjectRootInfo[]) {
    this.projects = projects;
    this.roots = roots;
  }

  /** Forgets what was sent so far. */
  resetCalls() {
    this.subscriptions = [];
    this.unsubscriptions = [];
    this.calls = { connect: 0, listProjects: 0, getAttention: 0, retryNow: 0 };
  }

  private setState(state: ConnectionState) {
    this.state = state;
    this.callbacks.onStateChanged?.(state);
  }

  /** As the real hub's invoke: a call on a connection that is not connected throws. */
  private invoke() {
    if (this.state !== 'connected') throw new Error("Cannot send data if the connection is not in the 'Connected' State.");
  }

  /** The connection is lost; the hub is retrying. */
  async drop() { this.setState('reconnecting'); await flush(); }
  /** A retry succeeded: a new connection, which holds no subscriptions. */
  async reconnect() { this.setState('connected'); await flush(); }

  setCallbacks(callbacks: HubCallbacks) { this.callbacks = callbacks; }
  async connect() {
    this.calls.connect++;
    if (this.failConnect) { this.state = 'disconnected'; throw new Error('unreachable'); }
    this.setState('connected');
  }
  async disconnect() { this.setState('disconnected'); }
  retryNow() { this.calls.retryNow++; }
  async listProjects() { this.invoke(); this.calls.listProjects++; return this.projects; }
  async listProjectRoots() { this.invoke(); return this.roots; }
  async listProfiles(): Promise<ProfileInfo[]> { this.invoke(); return []; }
  async getAttention() { this.invoke(); this.calls.getAttention++; return []; }
  async subscribeProject(projectId: string, fromOffset: number) {
    this.invoke();
    this.subscriptions.push({ projectId, fromOffset });
    this.replays.push(new Replay(this, projectId, fromOffset));
  }

  /** The replays of one project's subscriptions, oldest first. */
  replaysOf(projectId: string) { return this.replays.filter(r => r.projectId === projectId); }
  async unsubscribeProject(projectId: string) { this.invoke(); this.unsubscriptions.push(projectId); }
  async replyAndResume(projectId: string, text: string) { this.replies.push({ projectId, text }); }
  async answerQuestion(projectId: string, requestId: string, answers: Record<string, string>) {
    this.answers.push({ projectId, requestId, answers });
  }
  async respondToPermission(projectId: string, requestId: string, decision: PermissionDecision) {
    this.decisions.push({ projectId, requestId, decision });
  }
  async markSeen(projectId: string) { this.seen.push(projectId); }
  async createProject(_profileName: string, rootName: string, actionName: string | null, inputs: Record<string, unknown>) {
    this.invoke();
    this.created.push({ rootName, actionName, inputs });
    return status(`new${this.created.length}`, 'Running');
  }
}

/** Lets pending promise continuations run. */
export const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));

export const project = (id: string, name: string, state: ProjectState, updatedAt: string): ProjectSummary => ({
  Id: id, Name: name, State: state, UpdatedAt: updatedAt, RootName: 'work', ProfileName: 'Default',
});
export const root: ProjectRootInfo = { Name: 'work', ProfileName: 'Default', Actions: [] } as unknown as ProjectRootInfo;
export const status = (id: string, state: ProjectState) =>
  ({ Id: id, Name: id, State: state, UpdatedAt: '2026-09-24T12:00:00Z', RootName: 'work', ProfileName: 'Default' }) as ProjectStatus;

/** Puts the hubs in the store as servers, keyed by their id, and connects each. */
export async function connectServers(hubs: Record<string, FakeHub>) {
  const conn = (id: string, hub: FakeHub): ServerConnection => ({
    serverInfo: { Id: id, Name: `Server ${id}`, Type: 'local', State: 'Running' } as ServerInfo,
    hub: hub as unknown as ServerConnection['hub'],
    connectionState: 'disconnected', projects: [], roots: [], profiles: [],
  });
  useAppStore.setState({ serverConnections: Object.entries(hubs).map(([id, hub]) => conn(id, hub)) });
  for (const id of Object.keys(hubs)) await useAppStore.getState().connectServer(id);
}
