/**
 * Global app state using Zustand.
 * Matches Avalonia's data model: Profile → Root:Server → Projects.
 */
import { create } from 'zustand';
import { GodModeHub, type ConnectionState, type OutputMessage } from '../signalr/hub';
import type {
  ProjectSummary, ProjectStatus, ClaudeMessage, PermissionDecision, AttentionItem,
} from '../signalr/types';
import * as api from '../services/hostApi';
import type { AddServerRequest } from '../signalr/types';
import {
  type QuestionState, emptyQuestion, detectQuestionFromMessage,
  detectQuestionFromStatus, isQuestionMessage,
} from '../services/questionDetection';
import { projectKey, type ProjectKey } from './projectKey';
import {
  rebuildHierarchy, computeTotalWaiting, SIDEBAR_GROUP_ORDER,
  type ServerConnection, type SidebarGroupBy, type ProfileGroup,
} from './hierarchy';

export { projectKey, type ProjectKey };
/** The key of a transcript: a project's ProjectKey. */
export { projectKey as transcriptKey };
export type { ServerConnection, SidebarGroupBy, SidebarItem, RootGroup, ProfileGroup } from './hierarchy';
export { isListed } from './hierarchy';

// ── Persisted dismiss tracking ─────────────────────────────────
// Keyed by ProjectKey; the unversioned key held project IDs alone, which collide across servers
const DISMISSED_KEY = 'godmode-dismissed-projects-v2';
function loadDismissed(): Record<ProjectKey, true> {
  try {
    localStorage.removeItem('godmode-dismissed-projects');
    return JSON.parse(localStorage.getItem(DISMISSED_KEY) || '{}');
  } catch { return {}; }
}
function saveDismissed(dp: Record<ProjectKey, true>) {
  localStorage.setItem(DISMISSED_KEY, JSON.stringify(dp));
}

/** Marks a project's question dismissed or not; the same object when nothing changes. */
function withDismissed(dp: Record<ProjectKey, true>, key: ProjectKey, dismissed: boolean): Record<ProjectKey, true> {
  if (!!dp[key] === dismissed) return dp;
  const next = { ...dp };
  if (dismissed) next[key] = true; else delete next[key];
  return next;
}

/** The map without the keys given; the same object when it has none of them. */
function without<T>(map: Record<ProjectKey, T>, keys: ReadonlySet<ProjectKey>): Record<ProjectKey, T> {
  const gone = (Object.keys(map) as ProjectKey[]).filter(k => keys.has(k));
  if (gone.length === 0) return map;
  const next = { ...map };
  for (const k of gone) delete next[k];
  return next;
}

/** A server's project list with the project in it: replaced when listed already, else appended. */
function withProject(connections: ServerConnection[], serverId: string, project: ProjectSummary): ServerConnection[] {
  return connections.map(c => c.serverInfo.Id !== serverId ? c : {
    ...c,
    projects: c.projects.some(p => p.Id === project.Id)
      ? c.projects.map(p => p.Id === project.Id ? project : p)
      : [...c.projects, project],
  });
}

function summaryOf(status: ProjectStatus): ProjectSummary {
  return {
    Id: status.Id, Name: status.Name, State: status.State,
    UpdatedAt: status.UpdatedAt, CurrentQuestion: status.CurrentQuestion,
    RootName: status.RootName, ProfileName: status.ProfileName,
    PendingPermission: status.PendingPermission, PendingQuestion: status.PendingQuestion,
    PullRequest: status.PullRequest,
  };
}

// ── Transcripts (a project's output, per server) ───────────────

/**
 * Where a transcript's or a tile's lines end, and whose replay it takes (#239). `offset` is the byte
 * offset in the server's output.jsonl after the last line held, in the file's `generation` (null until
 * the server first names it): subscribing from both gets only what follows. `subscription` is the id of
 * the SubscribeProject last sent for it (null when none is): a batch or complete for any other is an
 * older subscription's, and changes nothing.
 */
export interface OutputPosition {
  offset: number;
  generation: string | null;
  subscription: string | null;
}

/**
 * A project's output as this client holds it. `phase`: 'replaying' from subscribe until its
 * subscription's OutputReplayComplete, then 'live'; 'idle' once unsubscribed, when the transcript is
 * kept to resume from.
 */
export interface Transcript extends OutputPosition {
  messages: ClaudeMessage[];
  phase: 'idle' | 'replaying' | 'live';
}

const emptyTranscript: Transcript = { messages: [], offset: 0, generation: null, subscription: null, phase: 'idle' };

/** The ids this page gives its subscriptions: unique on the page, which is all a connection needs. */
let subscriptions = 0;
const newSubscription = () => `s${++subscriptions}`;

/** Whether a batch or complete answers the subscription a transcript or tile holds now, and so is for it. */
const answers = (p: OutputPosition, subscription: string) => p.subscription === subscription;

/** An attention item and the server it is from: attention is per server, merged here. */
export interface ServerAttentionItem extends AttentionItem {
  serverId: string;
}

/**
 * Replaces one server's items in the merged list, keeping it oldest first and one item per
 * ProjectKey (the last a server lists, should it list a project twice). Oldest by time, not by text:
 * the server writes no trailing zeros in fractional seconds, so `…:00.5Z` is later than `…:00.52Z` as text.
 */
function mergeAttention(all: ServerAttentionItem[], serverId: string, items: AttentionItem[]): ServerAttentionItem[] {
  const fresh = new Map(items.map(i => [i.ProjectId, { ...i, serverId }]));
  return [...all.filter(i => i.serverId !== serverId), ...fresh.values()]
    .sort((a, b) => (Date.parse(a.Since) - Date.parse(b.Since))
      || projectKey(a.serverId, a.ProjectId).localeCompare(projectKey(b.serverId, b.ProjectId)));
}

/** What is typed in an inbox item and not sent yet. */
export interface InboxDraft {
  reply: string;
  denyMessage: string;
}

/** How many turns a tile asks for (tail mode: subscribe from -N). */
export const TILE_TAIL_TURNS = 2;

/** What a replayed batch or complete says of itself: the subscription it answers, and the file's generation. */
interface ReplayOf {
  subscription: string;
  generation: string;
}

/**
 * Appends the lines past the transcript's offset, so a line received twice is kept once. A batch
 * from offset 0, or in another generation, is the whole file, so what was held is dropped first; a
 * batch that starts past the offset would leave a gap, and is not applied.
 */
function appendLines(t: Transcript, lines: OutputMessage[], batch?: ReplayOf & { fromOffset: number }): { transcript: Transcript; added: ClaudeMessage[] } {
  const base = batch && (batch.fromOffset === 0 || batch.generation !== t.generation)
    ? { ...t, messages: [], offset: 0, generation: batch.generation }
    : t;
  if (batch && batch.fromOffset > base.offset) return { transcript: t, added: [] };
  const fresh = lines.filter(l => l.offset > base.offset);
  if (fresh.length === 0) return { transcript: base, added: [] };
  const added = fresh.map(l => l.message);
  return { transcript: { ...base, messages: [...base.messages, ...added], offset: fresh[fresh.length - 1].offset }, added };
}

/**
 * The question a held transcript is waiting on, as detection over a full replay would find it:
 * the current turn's messages (those after the last user message), then the server's status.
 * None when the project is not waiting or its question was dismissed.
 */
function heldQuestion(
  messages: ClaudeMessage[], project: ProjectSummary | undefined, dismissed: boolean | undefined, lastInputSentAt: number,
): QuestionState {
  if (!project || dismissed || (project.State !== 'WaitingInput' && project.State !== 'Idle')) return emptyQuestion;
  let turnStart = messages.length;
  while (turnStart > 0 && messages[turnStart - 1].type !== 'user') turnStart--;
  let question = emptyQuestion;
  for (const message of messages.slice(turnStart)) {
    question = detectQuestionFromMessage(message, question, lastInputSentAt, dismissed) ?? question;
  }
  return detectQuestionFromStatus(project.State, project.CurrentQuestion, project.Name, question, lastInputSentAt, messages) ?? question;
}

// ── Active page (replaces modal booleans) ─────────────────────
export type ActivePage =
  | { type: 'appSettings' }
  | { type: 'addServer' }
  | { type: 'editServer'; serverId: string }
  | { type: 'createProject'; context?: { serverId: string; rootName: string } };

// ── Store interface ────────────────────────────────────────────

interface AppState {
  // Mobile detection
  isMobile: boolean;
  setIsMobile: (mobile: boolean) => void;
  // Raw server connections
  serverConnections: ServerConnection[];
  getConnection: (serverId: string) => ServerConnection | undefined;
  getHub: (serverId: string) => GodModeHub | undefined;

  // Computed hierarchy (rebuilt from serverConnections)
  profileGroups: ProfileGroup[];
  inactiveServers: ServerConnection[];
  profileFilterOptions: string[];
  profileFilter: string;
  setProfileFilter: (filter: string) => void;

  // Sidebar grouping
  sidebarGroupBy: SidebarGroupBy;
  cycleSidebarGroupBy: () => void;

  // Server lifecycle
  loadServers: () => Promise<void>;
  addServer: (req: AddServerRequest) => Promise<void>;
  removeServer: (serverId: string) => Promise<void>;
  connectServer: (serverId: string) => Promise<void>;
  disconnectServer: (serverId: string) => Promise<void>;
  startServer: (serverId: string) => Promise<void>;
  /**
   * The server's lists again, and what is held reconciled with them (#239): each listed project's
   * question as its status says, and a project gone gets what a ProjectDeleted would.
   */
  refreshProjects: (serverId: string) => Promise<void>;
  /**
   * By server: its project list is the one it gave on this connection. Until it is, a project missing
   * from it may be there, so no view says it is not found.
   */
  projectsListed: Record<string, true>;
  /** Retries every server that is not connected, now: the page woke, or the network is back. */
  retryServers: () => void;

  // Selected project (by serverId + projectId)
  selectedProject: { serverId: string; projectId: string } | null;
  selectProject: (serverId: string, projectId: string) => void;
  clearSelection: () => void;
  /**
   * Lists and opens a project this client created, from its own createProject result. The
   * ProjectCreated broadcast goes to every client and only lists it, so nobody else's view moves.
   */
  openCreatedProject: (serverId: string, status: ProjectStatus) => void;

  // Project output: transcripts by ProjectKey; outputMessages is the selected one's.
  // The store owns subscriptions: a component opens and closes one, and the store (re)subscribes each
  // open one whenever its server connects, from its own offset (#171)
  transcripts: Record<ProjectKey, Transcript>;
  /**
   * Opens a project's output, resuming from the offset of the transcript held (0 if none). Resolves
   * after the replay; while not connected, at once, and the store subscribes when the server connects.
   */
  subscribeOutput: (serverId: string, projectId: string) => Promise<void>;
  /** Stops a project's live output. The transcript is kept, to resume from. */
  unsubscribeOutput: (serverId: string, projectId: string) => Promise<void>;
  /** Opens a tile on the last `turns` turns of a project's output (tileMessages, not a transcript). */
  subscribeTail: (serverId: string, projectId: string, turns: number) => Promise<void>;
  /** Closes a tile. */
  unsubscribeTail: (serverId: string, projectId: string) => Promise<void>;
  outputMessages: ClaudeMessage[];

  // Question state
  question: QuestionState;
  lastInputSentAt: number;
  setQuestion: (q: QuestionState) => void;
  dismissQuestion: () => void;
  markInputSent: () => void;

  // Permission prompts and AskUserQuestion (ProjectSummary.PendingPermission / PendingQuestion). These, markSeen
  // and replyAndResume reject, saying so, when the server has left the list: the caller keeps what was typed
  respondToPermission: (serverId: string, projectId: string, requestId: string, decision: PermissionDecision) => Promise<void>;
  answerQuestion: (serverId: string, projectId: string, requestId: string, answers: Record<string, string>) => Promise<void>;

  // What needs the user, across every connected server, oldest first. Key an item by projectKey(serverId, ProjectId)
  attention: ServerAttentionItem[];
  /** What the phone's home screen shows: the inbox (the default), or the project list. A wide screen shows both. */
  homeView: 'inbox' | 'projects';
  setHomeView: (view: 'inbox' | 'projects') => void;
  /** The inbox item a notification tap opened, and when (Date.now()): a new object for each tap, even on the same item. */
  inboxFocus: { key: ProjectKey; at: number } | null;
  /** Shows the inbox on one item: a tapped notification. */
  openInboxItem: (serverId: string, projectId: string) => void;
  markSeen: (serverId: string, projectId: string) => Promise<void>;
  /** Answers a project whether its claude runs or not (resuming it if needed). */
  replyAndResume: (serverId: string, projectId: string, text: string) => Promise<void>;
  /**
   * The inbox items' unsent text, by ProjectKey: held here rather than in the item, so it survives the
   * item remounting (the phone's home left and come back to, the pane collapsed, a rotation) (#240).
   */
  inboxDrafts: Record<ProjectKey, InboxDraft>;
  setInboxDraft: (serverId: string, projectId: string, draft: Partial<InboxDraft>) => void;

  // Per-project question tracking, by ProjectKey. dismissedProjects is persisted and holds only dismissed ones
  projectQuestions: Record<ProjectKey, boolean>;
  dismissedProjects: Record<ProjectKey, true>;

  // Notification badges
  totalWaitingCount: number;

  // Tile view
  isTileView: boolean;
  setTileView: (tile: boolean) => void;
  // By ProjectKey
  tileMessages: Record<ProjectKey, ClaudeMessage[]>;
  tileLoading: Record<ProjectKey, boolean>;
  /** The open tiles, each with the offset after its last line (-turns until it has one), which it resumes from. */
  tiles: Record<ProjectKey, OutputPosition>;
  setTileLoading: (key: ProjectKey, loading: boolean) => void;
  clearTileMessages: () => void;

  // UI pages (replaces modals)
  activePage: ActivePage | null;
  setActivePage: (page: ActivePage | null) => void;
  closePage: () => void;

  // Feature visibility
  featureProfiles: boolean;
  setFeatureFlag: (flag: 'featureProfiles', value: boolean) => void;
}

// ── Store ──────────────────────────────────────────────────────

/** The selected project's question is gone: dismissed by the user, or answered by input sent. */
function questionCleared(state: AppState, dismissed: boolean): Partial<AppState> {
  const sel = state.selectedProject;
  const key = sel && projectKey(sel.serverId, sel.projectId);
  const pq = key ? { ...state.projectQuestions, [key]: false } : state.projectQuestions;
  const dp = key ? withDismissed(state.dismissedProjects, key, dismissed) : state.dismissedProjects;
  if (dp !== state.dismissedProjects) saveDismissed(dp);
  const total = computeTotalWaiting(state.serverConnections, pq, dp);
  return { question: emptyQuestion, lastInputSentAt: Date.now(), projectQuestions: pq, dismissedProjects: dp, totalWaitingCount: total };
}

/**
 * What is held for projects their server no longer has, dropped, whether it said so (ProjectDeleted)
 * or a list after a reconnect shows it: questions, drafts, tiles and output. A transcript still open
 * stays open, empty and unsubscribed, so it is subscribed afresh should its project come back (created
 * again with the same ID). The selection stays, and its view says the project is not found.
 * The caller works out what depends on the lists (totalWaitingCount among it).
 */
function forgotten(state: AppState, keys: ReadonlySet<ProjectKey>): Partial<AppState> {
  if (keys.size === 0) return {};
  const transcripts: Record<ProjectKey, Transcript> = {};
  for (const [key, t] of Object.entries(state.transcripts) as [ProjectKey, Transcript][]) {
    if (!keys.has(key)) transcripts[key] = t;
    else if (t.phase !== 'idle') transcripts[key] = { ...emptyTranscript, phase: 'replaying' };
  }
  const dp = without(state.dismissedProjects, keys);
  if (dp !== state.dismissedProjects) saveDismissed(dp);
  const sel = state.selectedProject;
  return {
    transcripts, dismissedProjects: dp,
    projectQuestions: without(state.projectQuestions, keys), inboxDrafts: without(state.inboxDrafts, keys),
    tiles: without(state.tiles, keys), tileMessages: without(state.tileMessages, keys), tileLoading: without(state.tileLoading, keys),
    ...(sel && keys.has(projectKey(sel.serverId, sel.projectId)) ? { outputMessages: [], question: emptyQuestion } : {}),
  };
}

/** The server's keys, held anywhere in the store, whose project is not in its list. */
function goneKeys(state: AppState, serverId: string, projects: ProjectSummary[]): Set<ProjectKey> {
  const prefix = projectKey(serverId, '');
  const kept = new Set(projects.map(p => projectKey(serverId, p.Id)));
  const sel = state.selectedProject;
  const held = [
    state.transcripts, state.projectQuestions, state.dismissedProjects, state.inboxDrafts, state.tiles,
  ].flatMap(map => Object.keys(map) as ProjectKey[]);
  if (sel) held.push(projectKey(sel.serverId, sel.projectId));
  return new Set(held.filter(k => k.startsWith(prefix) && !kept.has(k)));
}

/** Whether a listed project is waiting on the user's answer to a question, as its status says. */
const asksQuestion = (p: ProjectSummary) => p.State === 'WaitingInput' || (p.State === 'Idle' && !!p.CurrentQuestion);

/** A project added to its server's list (replacing it if listed already), and what is derived from the lists. */
function listed(state: AppState, serverId: string, project: ProjectSummary): Partial<AppState> {
  const connections = withProject(state.serverConnections, serverId, project);
  const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
  const total = computeTotalWaiting(connections, state.projectQuestions, state.dismissedProjects);
  return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions, totalWaitingCount: total };
}

// Helper to persist sidebar groupBy
const GROUPBY_KEY = 'godmode-sidebar-groupby';
function loadGroupBy(): SidebarGroupBy {
  const v = localStorage.getItem(GROUPBY_KEY);
  return SIDEBAR_GROUP_ORDER.includes(v as SidebarGroupBy) ? v as SidebarGroupBy : 'profile';
}

// Retry each server that is not connected when the page is shown again, or the network is back
let watchingWake = false;
function watchWake(retry: () => void) {
  if (watchingWake || typeof document === 'undefined') return;
  watchingWake = true;
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') retry(); });
  window.addEventListener('online', retry);
}

export const useAppStore = create<AppState>((set, get) => {
  /**
   * Opens a tile on a project's output from fromOffset: -turns for a new tail, which starts empty, or
   * the tile's offset to resume, adding what follows. Subscribed when its server connects.
   */
  const openTail = async (serverId: string, projectId: string, fromOffset: number) => {
    const conn = get().getConnection(serverId);
    if (!conn) return;
    const key = projectKey(serverId, projectId);
    const connected = conn.connectionState === 'connected';
    // A new tail holds nothing yet, so no generation; a resume holds its lines, in theirs
    const tile: OutputPosition = {
      offset: fromOffset,
      generation: fromOffset < 0 ? null : get().tiles[key]?.generation ?? null,
      subscription: connected ? newSubscription() : null,
    };
    set(state => ({
      tileMessages: fromOffset < 0 ? { ...state.tileMessages, [key]: [] } : state.tileMessages,
      tileLoading: { ...state.tileLoading, [key]: true },
      tiles: { ...state.tiles, [key]: tile },
    }));
    if (!tile.subscription) return;
    await conn.hub.subscribeProject(projectId, fromOffset, tile.subscription, tile.generation);
  };

  /** The server's hub for a call the user makes. Rejects, saying why, when the server has left the list. */
  const hubFor = (serverId: string): GodModeHub => {
    const hub = get().getHub(serverId);
    if (!hub) throw new Error("This project's server is no longer in the server list");
    return hub;
  };

  /** Ends a subscription on the server. A lost connection has none to end: its server dropped them. */
  const unsubscribe = async (serverId: string, projectId: string) => {
    const conn = get().getConnection(serverId);
    if (conn?.connectionState === 'connected') await conn.hub.unsubscribeProject(projectId);
  };

  return {
  // Mobile detection
  isMobile: false,
  setIsMobile: (mobile) => set({ isMobile: mobile }),

  serverConnections: [],
  profileGroups: [],
  inactiveServers: [],
  profileFilterOptions: ['All'],
  profileFilter: 'All',

  getConnection: (serverId) => get().serverConnections.find(c => c.serverInfo.Id === serverId),
  getHub: (serverId) => get().serverConnections.find(c => c.serverInfo.Id === serverId)?.hub,

  setProfileFilter: (filter) => {
    const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(get().serverConnections, filter, get().sidebarGroupBy);
    set({ profileFilter: filter, profileGroups, inactiveServers, profileFilterOptions });
  },

  // Sidebar grouping
  sidebarGroupBy: loadGroupBy(),
  cycleSidebarGroupBy: () => {
    const current = get().sidebarGroupBy;
    const idx = SIDEBAR_GROUP_ORDER.indexOf(current);
    const next = SIDEBAR_GROUP_ORDER[(idx + 1) % SIDEBAR_GROUP_ORDER.length];
    localStorage.setItem(GROUPBY_KEY, next);
    const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(get().serverConnections, get().profileFilter, next);
    set({ sidebarGroupBy: next, profileGroups, inactiveServers, profileFilterOptions });
  },

  // ── Server lifecycle ──────────────────────────────────────

  loadServers: async () => {
    console.info('[store] loadServers: waiting for host API');
    watchWake(() => get().retryServers());
    await api.waitUntilReady();
    try {
      const servers = await api.fetchServers();
      console.info(`[store] loadServers: fetched ${servers.length} servers:`, servers.map(s => `${s.Name}(${s.State})`));
      const existing = get().serverConnections;

      // Preserve hubs and connection state for servers that are still present
      const connections: ServerConnection[] = servers.map(s => {
        const prev = existing.find(c => c.serverInfo.Id === s.Id);
        if (prev) {
          return { ...prev, serverInfo: s };
        }
        return {
          serverInfo: s,
          hub: new GodModeHub(),
          connectionState: 'disconnected' as ConnectionState,
          projects: [],
          roots: [],
          profiles: [],
        };
      });

      const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, get().profileFilter, get().sidebarGroupBy);
      console.info(`[store] loadServers: ${profileGroups.length} profiles, ${inactiveServers.length} inactive`);
      // A server that left the list takes its inbox items with it: nothing could answer them
      set(state => {
        const attention = state.attention.filter(i => connections.some(c => c.serverInfo.Id === i.serverId));
        return {
          serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions,
          attention: attention.length === state.attention.length ? state.attention : attention,
        };
      });

      // Subscribe to SSE events (once)
      if (existing.length === 0) {
        console.info('[store] loadServers: subscribing to events');
        api.subscribeEvents((type) => {
          if (type === 'serversChanged') {
            console.info('[store] SSE: serversChanged');
            get().loadServers();
          }
        });
      }

      // Auto-connect to servers that aren't already connected
      // Skip codespaces that are clearly stopped (they'd need starting first)
      for (const conn of connections) {
        if (conn.connectionState !== 'disconnected') continue;
        if (conn.serverInfo.Type === 'github' && conn.serverInfo.State === 'Stopped') continue;
        console.info(`[store] Auto-connecting to ${conn.serverInfo.Name} (${conn.serverInfo.Id})`);
        get().connectServer(conn.serverInfo.Id).catch(err =>
          console.warn(`[store] Auto-connect to ${conn.serverInfo.Name} failed:`, err)
        );
      }
    } catch (err) {
      console.error('[store] Failed to load servers:', err);
    }
  },

  addServer: async (req) => {
    await api.addServer(req);
    await get().loadServers();
    set({ activePage: null });
  },

  removeServer: async (serverId) => {
    const conn = get().getConnection(serverId);
    if (conn) conn.hub.disconnect();
    try {
      await api.removeServer(serverId);
    } catch (err) {
      console.error('Failed to remove server:', err);
    }
    await get().loadServers();
    set({ activePage: null });
  },

  startServer: async (serverId) => {
    try {
      await api.startServer(serverId);
      // SSE will push serversChanged events as the server transitions states
    } catch (err) {
      console.error('Failed to start server:', err);
    }
  },

  connectServer: async (serverId) => {
    console.info(`[store] connectServer: ${serverId}`);
    const state = get();
    const conn = state.serverConnections.find(c => c.serverInfo.Id === serverId);
    if (!conn) { console.warn(`[store] connectServer: no connection found for ${serverId}`); return; }

    const updateConn = (updates: Partial<ServerConnection>) => {
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId ? { ...c, ...updates } : c
        );
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
        return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions };
      });
    };

    /**
     * Output lines for a project: a replayed batch, taken only by the transcript or tile whose
     * subscription it answers, or one live line, taken by those whose replay is complete.
     */
    const receiveOutput = (projectId: string, lines: OutputMessage[], batch?: ReplayOf & { fromOffset: number }) => {
      const key = projectKey(serverId, projectId);
      set(state => {
        const updates: Partial<AppState> = {};
        const messages = lines.map(l => l.message);

        if (!state.dismissedProjects[key] && messages.some(isQuestionMessage)) {
          const pq = { ...state.projectQuestions, [key]: true };
          updates.projectQuestions = pq;
          updates.totalWaitingCount = computeTotalWaiting(state.serverConnections, pq, state.dismissedProjects);
        }

        const held = state.transcripts[key];
        if (held && (batch ? answers(held, batch.subscription) : held.phase === 'live')) {
          const { transcript, added } = appendLines(held, lines, batch);
          updates.transcripts = { ...state.transcripts, [key]: transcript };
          const sel = state.selectedProject;
          if (sel?.serverId === serverId && sel.projectId === projectId) {
            updates.outputMessages = transcript.messages;
            let question = state.question;
            for (const message of added) {
              question = detectQuestionFromMessage(message, question, state.lastInputSentAt, state.dismissedProjects[key]) ?? question;
            }
            if (question !== state.question) updates.question = question;
          }
        }

        // An open tile takes its own subscription's replay while loading, then live lines: those past its
        // offset. A batch in another generation than the tile's lines replaces them: the server replayed from 0
        const tile = state.tiles[key];
        const loading = !!state.tileLoading[key];
        if (state.isTileView && tile && (batch ? loading && answers(tile, batch.subscription) : !loading)) {
          const restart = batch !== undefined && batch.generation !== tile.generation;
          const offset = restart ? 0 : tile.offset;
          const fresh = lines.filter(l => l.offset > offset);
          if (fresh.length > 0 || restart) {
            const kept = restart ? [] : state.tileMessages[key] ?? [];
            updates.tileMessages = { ...state.tileMessages, [key]: [...kept, ...fresh.map(l => l.message)] };
            updates.tiles = { ...state.tiles, [key]: {
              ...tile, offset: fresh.length > 0 ? fresh[fresh.length - 1].offset : offset, generation: batch?.generation ?? tile.generation,
            } };
          }
        }
        return updates;
      });
    };

    /** A project deleted on this server: drop it and what is held for it (the selection stays, and shows it is gone). */
    const removeProject = (projectId: string) => {
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId
            ? { ...c, projects: c.projects.filter(p => p.Id !== projectId) }
            : c
        );
        const updates = forgotten(state, new Set([projectKey(serverId, projectId)]));
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
        const total = computeTotalWaiting(connections, updates.projectQuestions ?? state.projectQuestions, updates.dismissedProjects ?? state.dismissedProjects);
        return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions, ...updates, totalWaitingCount: total };
      });
    };

    /**
     * A project created on this server is listed. One whose transcript is still open, empty, from a
     * project that had its ID (deleted, then created again) is subscribed afresh.
     */
    const addProject = (project: ProjectSummary) => {
      set(state => listed(state, serverId, project));
      const held = get().transcripts[projectKey(serverId, project.Id)];
      if (held && held.phase !== 'idle' && held.subscription === null && get().getConnection(serverId)?.connectionState === 'connected') {
        get().subscribeOutput(serverId, project.Id).catch(err => console.error('[store] subscribe failed:', serverId, err));
      }
    };

    /**
     * On each connect, the first or after a lost connection: the lists again, and every open
     * transcript and tile subscribed from its offset, as a new connection holds no subscriptions.
     * The one place that resubscribes; components only open and close (#171). What was open when the
     * connection came was subscribed on the lost one, or not yet; what opens from then on subscribes
     * on this one itself (a tile the new list adds, say), so is left alone (#239).
     */
    const catchUp = async () => {
      const prefix = projectKey(serverId, '');
      const openThen = <T extends OutputPosition>(map: Record<ProjectKey, T>, open: (p: T) => boolean) =>
        new Map((Object.entries(map) as [ProjectKey, T][])
          .filter(([key, p]) => key.startsWith(prefix) && open(p)).map(([key, p]) => [key, p.subscription]));
      const transcriptsThen = openThen(get().transcripts, t => t.phase !== 'idle');
      const tilesThen = openThen(get().tiles, () => true);
      /** Still open, on the subscription it had when the connection came. */
      const unchanged = (then: Map<ProjectKey, string | null>, key: ProjectKey, now: OutputPosition | undefined) =>
        now !== undefined && then.has(key) && then.get(key) === now.subscription;

      await get().refreshProjects(serverId);
      // Of the projects listed now: one deleted meanwhile is not subscribed
      const state = get();
      const projects = state.getConnection(serverId)?.projects ?? [];
      const keyed = projects.map(p => ({ projectId: p.Id, key: projectKey(serverId, p.Id) }));
      const isOpen = (key: ProjectKey) => state.transcripts[key] !== undefined && state.transcripts[key].phase !== 'idle';
      const transcripts = keyed.filter(({ key }) => isOpen(key) && unchanged(transcriptsThen, key, state.transcripts[key]));
      // One subscription per project: a transcript open on it wins over a tile
      const tiles = keyed.filter(({ key }) => !isOpen(key) && unchanged(tilesThen, key, state.tiles[key]));
      await Promise.all([
        ...transcripts.map(({ projectId }) => get().subscribeOutput(serverId, projectId)),
        ...tiles.map(({ projectId, key }) => openTail(serverId, projectId, state.tiles[key].offset)),
      ].map(p => p.catch(err => console.error('[store] resubscribe failed:', serverId, err))));
      // What the selected project waits on, from its state now and the lines replayed
      set(state => {
        const sel = state.selectedProject;
        if (sel?.serverId !== serverId) return {};
        const key = projectKey(serverId, sel.projectId);
        const project = state.getConnection(serverId)?.projects.find(p => p.Id === sel.projectId);
        return { question: heldQuestion(state.outputMessages, project, state.dismissedProjects[key], state.lastInputSentAt) };
      });
    };
    let caughtUp: Promise<void> = Promise.resolve();

    conn.hub.setCallbacks({
      onStateChanged: (connectionState) => {
        updateConn({ connectionState });
        // The list held is from a connection before this one, until the catch-up takes it again
        if (connectionState !== 'connected' && get().projectsListed[serverId]) {
          set(state => {
            const projectsListed = { ...state.projectsListed };
            delete projectsListed[serverId];
            return { projectsListed };
          });
        }
        if (connectionState === 'connected') caughtUp = catchUp();
        if (connectionState === 'connected') {
          // A reconnect may have missed pushes: take the whole list again
          conn.hub.getAttention()
            .then(items => set(state => ({ attention: mergeAttention(state.attention, serverId, items) })))
            .catch(err => console.error('[store] getAttention failed:', serverId, err));
        } else if (connectionState === 'disconnected') {
          set(state => ({ attention: mergeAttention(state.attention, serverId, []) }));
        }
      },
      onAttentionChanged: (items) => set(state => ({ attention: mergeAttention(state.attention, serverId, items) })),
      // Every client hears of every created project: list it, and leave the view alone (#170)
      onProjectCreated: (status) => addProject(summaryOf(status)),
      onProjectDeleted: removeProject,
      onStatusChanged: (_projectId, status) => {
        set(state => {
          const connections = state.serverConnections.map(c =>
            c.serverInfo.Id === serverId
              ? { ...c, projects: c.projects.map(p => p.Id === status.Id
                  ? {
                      ...p, State: status.State, UpdatedAt: status.UpdatedAt, CurrentQuestion: status.CurrentQuestion,
                      PendingPermission: status.PendingPermission, PendingQuestion: status.PendingQuestion,
                      PullRequest: status.PullRequest,
                    }
                  : p) }
              : c
          );
          const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);

          const key = projectKey(serverId, status.Id);
          const sel = state.selectedProject;
          let questionUpdate: Partial<AppState> = {};
          let pq = state.projectQuestions;
          if (sel?.serverId === serverId && sel?.projectId === status.Id && !state.dismissedProjects[key]) {
            const isWaitingOrIdle = status.State === 'WaitingInput' || status.State === 'Idle';
            if (isWaitingOrIdle) {
              const project = connections.find(c => c.serverInfo.Id === serverId)?.projects.find(p => p.Id === status.Id);
              if (project) {
                const detected = detectQuestionFromStatus(
                  status.State, status.CurrentQuestion, project.Name,
                  state.question, state.lastInputSentAt, state.outputMessages,
                );
                if (detected) {
                  questionUpdate = { question: detected };
                  pq = { ...pq, [key]: detected.isActive };
                }
              }
            } else if (state.question.isActive) {
              questionUpdate = { question: emptyQuestion };
              pq = { ...pq, [key]: false };
            }
          }

          let dp = state.dismissedProjects;
          if (status.State === 'Running') {
            pq = { ...pq, [key]: false };
            dp = withDismissed(dp, key, false);
            if (dp !== state.dismissedProjects) saveDismissed(dp);
          }

          const total = computeTotalWaiting(connections, pq, dp);
          return {
            serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions,
            projectQuestions: pq, dismissedProjects: dp, totalWaitingCount: total,
            ...questionUpdate,
          };
        });
      },
      // A live line only counts once its subscription's replay is complete: one broadcast before
      // that is in output.jsonl already, and so in the replay
      onOutputReceived: (projectId, line) => receiveOutput(projectId, [line]),
      onOutputBatch: (projectId, subscription, generation, fromOffset, lines) =>
        receiveOutput(projectId, lines, { subscription, generation, fromOffset }),
      // Ends the replay of the transcript or tile whose subscription it answers, and no other's
      onOutputReplayComplete: (projectId, subscription, generation, offset) => {
        const key = projectKey(serverId, projectId);
        set(state => {
          const updates: Partial<AppState> = {};
          const held = state.transcripts[key];
          if (held && answers(held, subscription)) {
            // Another generation, or output.jsonl shorter than what is held: the transcript is not from this file
            const transcript: Transcript = generation !== held.generation || offset < held.offset
              ? { ...emptyTranscript, offset, generation, subscription, phase: 'live' }
              : { ...held, phase: 'live' };
            updates.transcripts = { ...state.transcripts, [key]: transcript };
            const sel = state.selectedProject;
            if (sel?.serverId === serverId && sel.projectId === projectId) updates.outputMessages = transcript.messages;
          }
          const tile = state.tiles[key];
          if (state.isTileView && tile && answers(tile, subscription)) {
            updates.tileLoading = { ...state.tileLoading, [key]: false };
            // No batch came in this generation: the tile's lines, if any, are not from this file. A tail
            // that replayed nothing resumes from the end of the file
            const restart = generation !== tile.generation;
            if (restart) updates.tileMessages = { ...state.tileMessages, [key]: [] };
            updates.tiles = { ...state.tiles, [key]: { ...tile, generation, offset: restart ? offset : Math.max(tile.offset, offset) } };
          }
          return updates;
        });
      },
      onCreationProgress: () => {},
    });

    try {
      const hubUrl = api.getHubUrl(serverId);
      const hubOptions = api.getHubOptions(serverId);
      console.info(`[store] connectServer: hub.connect(${hubUrl})...`);
      await conn.hub.connect(hubUrl, hubOptions);
      console.info(`[store] connectServer: connected, catching up...`);
      await caughtUp;
      console.info(`[store] connectServer: ${serverId} ready`);
    } catch (err) {
      console.error(`[store] connectServer ${serverId} failed:`, err);
      updateConn({ connectionState: 'disconnected' });
    }
  },

  disconnectServer: async (serverId) => {
    const conn = get().getConnection(serverId);
    if (conn) {
      await conn.hub.disconnect();
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId
            ? { ...c, projects: [], roots: [], profiles: [], connectionState: 'disconnected' as ConnectionState }
            : c
        );
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
        const total = computeTotalWaiting(connections, state.projectQuestions, state.dismissedProjects);
        return { serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions, totalWaitingCount: total };
      });
    }
  },

  retryServers: () => {
    for (const conn of get().serverConnections) {
      if (conn.connectionState === 'reconnecting') conn.hub.retryNow();
      // As loadServers' auto-connect: a stopped codespace needs starting first
      else if (conn.connectionState === 'disconnected' && !(conn.serverInfo.Type === 'github' && conn.serverInfo.State === 'Stopped')) {
        get().connectServer(conn.serverInfo.Id).catch(err => console.warn('[store] retry failed:', conn.serverInfo.Id, err));
      }
    }
  },

  projectsListed: {},
  refreshProjects: async (serverId) => {
    const conn = get().getConnection(serverId);
    if (!conn || conn.connectionState !== 'connected') return;
    try {
      console.info(`[store] refreshProjects: ${serverId}`);
      const [projects, roots, profiles] = await Promise.all([
        conn.hub.listProjects(),
        conn.hub.listProjectRoots(),
        conn.hub.listProfiles(),
      ]);
      console.info(`[store] refreshProjects: ${serverId} -> ${projects.length} projects, ${roots.length} roots, ${profiles.length} profiles`);
      if (projects.length > 0) console.debug('[store] projects sample:', JSON.stringify(projects[0]));
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId ? { ...c, projects, roots, profiles } : c
        );
        // What is held for a project this server no longer has goes with it, as when it was deleted
        const updates = forgotten(state, goneKeys(state, serverId, projects));
        // What each listed project asks, as its status says now: a question answered elsewhere while
        // this client slept has left no event behind to clear it
        const pq = { ...(updates.projectQuestions ?? state.projectQuestions) };
        for (const p of projects) {
          const key = projectKey(serverId, p.Id);
          if (asksQuestion(p)) pq[key] = true; else delete pq[key];
        }
        const dp = updates.dismissedProjects ?? state.dismissedProjects;
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
        return {
          serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions,
          ...updates, projectQuestions: pq, totalWaitingCount: computeTotalWaiting(connections, pq, dp),
          projectsListed: { ...state.projectsListed, [serverId]: true },
        };
      });
    } catch (err) {
      console.error('Failed to refresh projects:', err);
    }
  },

  // ── Selection ─────────────────────────────────────────────

  selectedProject: null,
  selectProject: (serverId, projectId) => set(state => {
    const key = projectKey(serverId, projectId);
    const messages = state.transcripts[key]?.messages ?? [];
    const project = state.serverConnections.find(c => c.serverInfo.Id === serverId)?.projects.find(p => p.Id === projectId);
    return {
      selectedProject: { serverId, projectId }, activePage: null, outputMessages: messages,
      // A resume replays only what is new, so the question comes from what is held
      question: heldQuestion(messages, project, state.dismissedProjects[key], state.lastInputSentAt),
    };
  }),
  clearSelection: () => set({ selectedProject: null, outputMessages: [], question: emptyQuestion }),
  openCreatedProject: (serverId, status) => {
    // The call can return before or after the broadcast: listing it twice keeps one entry
    set(state => listed(state, serverId, summaryOf(status)));
    get().selectProject(serverId, status.Id);
  },

  // ── Output ────────────────────────────────────────────────

  transcripts: {},
  subscribeOutput: async (serverId, projectId) => {
    const conn = get().getConnection(serverId);
    if (!conn) return;
    const key = projectKey(serverId, projectId);
    const held = get().transcripts[key] ?? emptyTranscript;
    // Open now; subscribed when the server connects. Only this subscription's replay is taken from here on
    const subscription = conn.connectionState === 'connected' ? newSubscription() : null;
    set(state => ({ transcripts: { ...state.transcripts, [key]: { ...held, phase: 'replaying', subscription } } }));
    if (!subscription) return;
    await conn.hub.subscribeProject(projectId, held.offset, subscription, held.generation);
  },
  unsubscribeOutput: async (serverId, projectId) => {
    const key = projectKey(serverId, projectId);
    set(state => state.transcripts[key]
      ? { transcripts: { ...state.transcripts, [key]: { ...state.transcripts[key], phase: 'idle', subscription: null } } }
      : {});
    await unsubscribe(serverId, projectId);
  },
  subscribeTail: (serverId, projectId, turns) => openTail(serverId, projectId, -turns),
  unsubscribeTail: async (serverId, projectId) => {
    const key = projectKey(serverId, projectId);
    set(state => state.tiles[key] ? { tiles: without(state.tiles, new Set([key])) } : {});
    await unsubscribe(serverId, projectId);
  },
  outputMessages: [],

  // ── Questions ─────────────────────────────────────────────

  question: emptyQuestion,
  lastInputSentAt: 0,
  setQuestion: (q) => set({ question: q }),
  dismissQuestion: () => set(state => questionCleared(state, true)),
  markInputSent: () => set(state => questionCleared(state, false)),

  respondToPermission: async (serverId, projectId, requestId, decision) => {
    await hubFor(serverId).respondToPermission(projectId, requestId, decision);
  },
  answerQuestion: async (serverId, projectId, requestId, answers) => {
    await hubFor(serverId).answerQuestion(projectId, requestId, answers);
  },

  attention: [],
  homeView: 'inbox',
  setHomeView: (view) => set({ homeView: view }),
  inboxFocus: null,
  openInboxItem: (serverId, projectId) => set({
    activePage: null, selectedProject: null, outputMessages: [], question: emptyQuestion, homeView: 'inbox',
    inboxFocus: { key: projectKey(serverId, projectId), at: Date.now() },
  }),
  markSeen: async (serverId, projectId) => {
    await hubFor(serverId).markSeen(projectId);
  },
  replyAndResume: async (serverId, projectId, text) => {
    await hubFor(serverId).replyAndResume(projectId, text);
  },
  inboxDrafts: {},
  setInboxDraft: (serverId, projectId, patch) => set(state => {
    const key = projectKey(serverId, projectId);
    const draft: InboxDraft = { ...(state.inboxDrafts[key] ?? { reply: '', denyMessage: '' }), ...patch };
    const inboxDrafts = { ...state.inboxDrafts };
    if (draft.reply || draft.denyMessage) inboxDrafts[key] = draft;
    else delete inboxDrafts[key];
    return { inboxDrafts };
  }),

  projectQuestions: {},
  dismissedProjects: loadDismissed(),
  totalWaitingCount: 0,

  // ── Tile view ─────────────────────────────────────────────

  isTileView: false,
  setTileView: (tile) => set({ isTileView: tile, tileMessages: {}, tileLoading: {}, tiles: {}, selectedProject: null, outputMessages: [], question: emptyQuestion }),
  tileMessages: {},
  tileLoading: {},
  tiles: {},
  setTileLoading: (key, loading) => set(state => ({ tileLoading: { ...state.tileLoading, [key]: loading } })),
  clearTileMessages: () => set({ tileMessages: {}, tileLoading: {}, tiles: {} }),

  // ── UI pages ────────────────────────────────────────────────

  activePage: null,
  setActivePage: (page) => set({ activePage: page }),
  closePage: () => set({ activePage: null }),

  // Feature visibility (persisted to localStorage)
  featureProfiles: localStorage.getItem('godmode-feature-profiles') !== 'false',
  setFeatureFlag: (flag, value) => {
    localStorage.setItem(`godmode-${flag.replace('feature', 'feature-').toLowerCase()}`, String(value));
    set({ [flag]: value });
  },
  };
});
