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
import type { AddServerRequest } from '../services/hostApi';
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

/** Drops a server's keys whose project it no longer has; the same object when nothing is dropped. */
function pruneServer<T>(map: Record<ProjectKey, T>, serverId: string, projects: ProjectSummary[]): Record<ProjectKey, T> {
  const prefix = projectKey(serverId, '');
  const kept = new Set(projects.map(p => projectKey(serverId, p.Id)));
  const stale = (Object.keys(map) as ProjectKey[]).filter(k => k.startsWith(prefix) && !kept.has(k));
  if (stale.length === 0) return map;
  const next = { ...map };
  for (const k of stale) delete next[k];
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
 * A project's output as this client holds it. `offset` is the byte offset in the server's
 * output.jsonl after the last message held: subscribing from it gets only what follows.
 * `phase`: 'replaying' from subscribe until the server's OutputReplayComplete, then 'live'; 'idle'
 * once unsubscribed, when the transcript is kept to resume from.
 */
export interface Transcript {
  messages: ClaudeMessage[];
  offset: number;
  phase: 'idle' | 'replaying' | 'live';
}

/** An attention item and the server it is from: attention is per server, merged here. */
export interface ServerAttentionItem extends AttentionItem {
  serverId: string;
}

/**
 * Replaces one server's items in the merged list, keeping it oldest first and one item per
 * ProjectKey (the last a server lists, should it list a project twice).
 */
function mergeAttention(all: ServerAttentionItem[], serverId: string, items: AttentionItem[]): ServerAttentionItem[] {
  const fresh = new Map(items.map(i => [i.ProjectId, { ...i, serverId }]));
  return [...all.filter(i => i.serverId !== serverId), ...fresh.values()]
    .sort((a, b) => a.Since.localeCompare(b.Since)
      || projectKey(a.serverId, a.ProjectId).localeCompare(projectKey(b.serverId, b.ProjectId)));
}

/** How many turns a tile asks for (tail mode: subscribe from -N). */
export const TILE_TAIL_TURNS = 2;

/**
 * Appends the lines past the transcript's offset, so a line received twice is kept once. A batch
 * from offset 0 is the whole file, so what was held is dropped first; a batch that starts past the
 * offset would leave a gap, and is not applied.
 */
function appendLines(t: Transcript, lines: OutputMessage[], fromOffset?: number): { transcript: Transcript; added: ClaudeMessage[] } {
  const base = fromOffset === 0 ? { ...t, messages: [], offset: 0 } : t;
  if (fromOffset !== undefined && fromOffset > base.offset) return { transcript: t, added: [] };
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
  | { type: 'profileSettings' }
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
  refreshProjects: (serverId: string) => Promise<void>;

  // Selected project (by serverId + projectId)
  selectedProject: { serverId: string; projectId: string } | null;
  selectProject: (serverId: string, projectId: string) => void;
  clearSelection: () => void;
  /**
   * Lists and opens a project this client created, from its own createProject result. The
   * ProjectCreated broadcast goes to every client and only lists it, so nobody else's view moves.
   */
  openCreatedProject: (serverId: string, status: ProjectStatus) => void;

  // Project output: transcripts by ProjectKey; outputMessages is the selected one's
  transcripts: Record<ProjectKey, Transcript>;
  /** Subscribes to a project's output, resuming from the offset of the transcript held (0 if none). Resolves after the replay. */
  subscribeOutput: (serverId: string, projectId: string) => Promise<void>;
  /** Stops a project's live output. The transcript is kept, to resume from. */
  unsubscribeOutput: (serverId: string, projectId: string) => Promise<void>;
  /** Subscribes a tile to the last `turns` turns of a project's output (tileMessages, not a transcript). */
  subscribeTail: (serverId: string, projectId: string, turns: number) => Promise<void>;
  outputMessages: ClaudeMessage[];

  // Question state
  question: QuestionState;
  lastInputSentAt: number;
  setQuestion: (q: QuestionState) => void;
  dismissQuestion: () => void;
  markInputSent: () => void;

  // Permission prompts and AskUserQuestion (ProjectSummary.PendingPermission / PendingQuestion)
  respondToPermission: (serverId: string, projectId: string, requestId: string, decision: PermissionDecision) => Promise<void>;
  answerQuestion: (serverId: string, projectId: string, requestId: string, answers: Record<string, string>) => Promise<void>;

  // What needs the user, across every connected server, oldest first. Key an item by projectKey(serverId, ProjectId)
  attention: ServerAttentionItem[];
  /** What the phone's home screen shows: the inbox (the default), or the project list. A wide screen shows both. */
  homeView: 'inbox' | 'projects';
  setHomeView: (view: 'inbox' | 'projects') => void;
  markSeen: (serverId: string, projectId: string) => Promise<void>;
  /** Answers a project whether its claude runs or not (resuming it if needed). */
  replyAndResume: (serverId: string, projectId: string, text: string) => Promise<void>;

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
  setTileLoading: (key: ProjectKey, loading: boolean) => void;
  clearTileMessages: () => void;

  // UI pages (replaces modals)
  activePage: ActivePage | null;
  setActivePage: (page: ActivePage | null) => void;
  closePage: () => void;

  // Backward-compat setters (delegate to activePage)
  setShowAddServer: (show: boolean) => void;
  setShowCreateProject: (show: boolean, context?: { serverId: string; rootName: string }) => void;
  setEditServerId: (id: string | null) => void;
  setShowProfileSettings: (show: boolean) => void;
  setShowAppSettings: (show: boolean) => void;

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

export const useAppStore = create<AppState>((set, get) => ({
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
      set({ serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions });

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
    try {
      await api.addServer(req);
      await get().loadServers();
      set({ activePage: null });
    } catch (err) {
      console.error('Failed to add server:', err);
    }
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

    /** Output lines for a project: a replayed batch (with its fromOffset) or one live line. */
    const receiveOutput = (projectId: string, lines: OutputMessage[], fromOffset?: number) => {
      const key = projectKey(serverId, projectId);
      const replayed = fromOffset !== undefined;
      set(state => {
        const updates: Partial<AppState> = {};
        const messages = lines.map(l => l.message);

        if (!state.dismissedProjects[key] && messages.some(isQuestionMessage)) {
          const pq = { ...state.projectQuestions, [key]: true };
          updates.projectQuestions = pq;
          updates.totalWaitingCount = computeTotalWaiting(state.serverConnections, pq, state.dismissedProjects);
        }

        const held = state.transcripts[key];
        if (held && (replayed ? held.phase !== 'idle' : held.phase === 'live')) {
          const { transcript, added } = appendLines(held, lines, fromOffset);
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

        // A tile takes its replay while loading, then live lines
        if (state.isTileView && replayed === !!state.tileLoading[key]) {
          updates.tileMessages = { ...state.tileMessages, [key]: [...(state.tileMessages[key] ?? []), ...messages] };
        }
        return updates;
      });
    };

    /** A project deleted or archived on this server: drop it and what is held for it. */
    const removeProject = (projectId: string) => {
      const key = projectKey(serverId, projectId);
      set(state => {
        const connections = state.serverConnections.map(c =>
          c.serverInfo.Id === serverId
            ? { ...c, projects: c.projects.filter(p => p.Id !== projectId) }
            : c
        );
        const sel = state.selectedProject;
        const clearSel = sel?.serverId === serverId && sel?.projectId === projectId;
        const pq = { ...state.projectQuestions };
        delete pq[key];
        const dp = withDismissed(state.dismissedProjects, key, false);
        if (dp !== state.dismissedProjects) saveDismissed(dp);
        const transcripts = { ...state.transcripts };
        delete transcripts[key];
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
        const total = computeTotalWaiting(connections, pq, dp);
        return {
          serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions,
          projectQuestions: pq, dismissedProjects: dp, totalWaitingCount: total, transcripts,
          ...(clearSel ? { selectedProject: null, outputMessages: [], question: emptyQuestion } : {}),
        };
      });
    };

    const addProject = (project: ProjectSummary) => set(state => listed(state, serverId, project));

    conn.hub.setCallbacks({
      onStateChanged: (connectionState) => {
        updateConn({ connectionState });
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
      // Same as delete: remove from the active list
      onProjectArchived: removeProject,
      onProjectRestored: addProject,
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
      onOutputBatch: (projectId, fromOffset, lines) => receiveOutput(projectId, lines, fromOffset),
      onOutputReplayComplete: (projectId, offset) => {
        const key = projectKey(serverId, projectId);
        set(state => {
          const updates: Partial<AppState> = {};
          const held = state.transcripts[key];
          if (held && held.phase !== 'idle') {
            // output.jsonl is shorter than what is held: the transcript is not from this file
            const transcript: Transcript = offset < held.offset
              ? { messages: [], offset, phase: 'live' }
              : { ...held, phase: 'live' };
            updates.transcripts = { ...state.transcripts, [key]: transcript };
            const sel = state.selectedProject;
            if (sel?.serverId === serverId && sel.projectId === projectId) updates.outputMessages = transcript.messages;
          }
          if (state.isTileView) updates.tileLoading = { ...state.tileLoading, [key]: false };
          return updates;
        });
      },
      onCreationProgress: () => {},
      onProfilesChanged: () => {
        get().refreshProjects(serverId);
      },
    });

    try {
      const hubUrl = api.getHubUrl(serverId);
      const hubOptions = api.getHubOptions(serverId);
      console.info(`[store] connectServer: hub.connect(${hubUrl})...`);
      await conn.hub.connect(hubUrl, hubOptions);
      console.info(`[store] connectServer: connected, refreshing projects...`);
      await get().refreshProjects(serverId);
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
        // What is held for a project this server no longer has goes with it
        const pq = pruneServer(state.projectQuestions, serverId, projects);
        const dp = pruneServer(state.dismissedProjects, serverId, projects);
        if (dp !== state.dismissedProjects) saveDismissed(dp);
        const { profileGroups, inactiveServers, profileFilterOptions } = rebuildHierarchy(connections, state.profileFilter, state.sidebarGroupBy);
        const total = computeTotalWaiting(connections, pq, dp);
        return {
          serverConnections: connections, profileGroups, inactiveServers, profileFilterOptions,
          projectQuestions: pq, dismissedProjects: dp, totalWaitingCount: total,
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
    const hub = get().getHub(serverId);
    if (!hub) return;
    const key = projectKey(serverId, projectId);
    const held = get().transcripts[key] ?? { messages: [], offset: 0, phase: 'idle' };
    set(state => ({ transcripts: { ...state.transcripts, [key]: { ...held, phase: 'replaying' } } }));
    await hub.subscribeProject(projectId, held.offset);
  },
  unsubscribeOutput: async (serverId, projectId) => {
    const key = projectKey(serverId, projectId);
    set(state => state.transcripts[key]
      ? { transcripts: { ...state.transcripts, [key]: { ...state.transcripts[key], phase: 'idle' } } }
      : {});
    await get().getHub(serverId)?.unsubscribeProject(projectId);
  },
  subscribeTail: async (serverId, projectId, turns) => {
    const hub = get().getHub(serverId);
    if (!hub) return;
    const key = projectKey(serverId, projectId);
    set(state => ({
      tileMessages: { ...state.tileMessages, [key]: [] },
      tileLoading: { ...state.tileLoading, [key]: true },
    }));
    await hub.subscribeProject(projectId, -turns);
  },
  outputMessages: [],

  // ── Questions ─────────────────────────────────────────────

  question: emptyQuestion,
  lastInputSentAt: 0,
  setQuestion: (q) => set({ question: q }),
  dismissQuestion: () => set(state => questionCleared(state, true)),
  markInputSent: () => set(state => questionCleared(state, false)),

  respondToPermission: async (serverId, projectId, requestId, decision) => {
    await get().getHub(serverId)?.respondToPermission(projectId, requestId, decision);
  },
  answerQuestion: async (serverId, projectId, requestId, answers) => {
    await get().getHub(serverId)?.answerQuestion(projectId, requestId, answers);
  },

  attention: [],
  homeView: 'inbox',
  setHomeView: (view) => set({ homeView: view }),
  markSeen: async (serverId, projectId) => {
    await get().getHub(serverId)?.markSeen(projectId);
  },
  replyAndResume: async (serverId, projectId, text) => {
    await get().getHub(serverId)?.replyAndResume(projectId, text);
  },

  projectQuestions: {},
  dismissedProjects: loadDismissed(),
  totalWaitingCount: 0,

  // ── Tile view ─────────────────────────────────────────────

  isTileView: false,
  setTileView: (tile) => set({ isTileView: tile, tileMessages: {}, tileLoading: {}, selectedProject: null, outputMessages: [], question: emptyQuestion }),
  tileMessages: {},
  tileLoading: {},
  setTileLoading: (key, loading) => set(state => ({ tileLoading: { ...state.tileLoading, [key]: loading } })),
  clearTileMessages: () => set({ tileMessages: {}, tileLoading: {} }),

  // ── UI pages ────────────────────────────────────────────────

  activePage: null,
  setActivePage: (page) => set({ activePage: page }),
  closePage: () => set({ activePage: null }),

  // Backward-compat setters (delegate to activePage)
  setShowAddServer: (show) => set({ activePage: show ? { type: 'addServer' } : null }),
  setShowCreateProject: (show, context) => set({ activePage: show ? { type: 'createProject', context } : null }),
  setEditServerId: (id) => set({ activePage: id ? { type: 'editServer', serverId: id } : null }),
  setShowProfileSettings: (show) => set({ activePage: show ? { type: 'profileSettings' } : null }),
  setShowAppSettings: (show) => set({ activePage: show ? { type: 'appSettings' } : null }),

  // Feature visibility (persisted to localStorage)
  featureProfiles: localStorage.getItem('godmode-feature-profiles') !== 'false',
  setFeatureFlag: (flag, value) => {
    localStorage.setItem(`godmode-${flag.replace('feature', 'feature-').toLowerCase()}`, String(value));
    set({ [flag]: value });
  },
}));
